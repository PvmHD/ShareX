﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (C) 2007-2014 ShareX Developers

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using HelpersLib;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Web;
using System.Windows.Forms;
using UploadersLib.HelperClasses;

namespace UploadersLib
{
    public class Uploader
    {
        public static string UserAgent = string.Format("{0} {1}", Application.ProductName, Application.ProductVersion);

        public delegate void ProgressEventHandler(ProgressManager progress);

        public event ProgressEventHandler ProgressChanged;

        public List<string> Errors { get; private set; }
        public bool IsUploading { get; protected set; }
        public int BufferSize { get; set; }
        public bool AllowReportProgress { get; set; }
        public bool SuppressWebExceptions { get; set; }

        protected bool stopUpload;

        public Uploader()
        {
            Errors = new List<string>();
            IsUploading = false;
            BufferSize = 8192;
            AllowReportProgress = true;

            ServicePointManager.DefaultConnectionLimit = 25;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        protected void OnProgressChanged(ProgressManager progress)
        {
            if (ProgressChanged != null)
            {
                ProgressChanged(progress);
            }
        }

        public string ToErrorString()
        {
            return string.Join("\r\n", Errors.ToArray());
        }

        public virtual void StopUpload()
        {
            if (IsUploading)
            {
                stopUpload = true;
            }
        }

        #region Request methods

        protected string SendRequest(HttpMethod method, string url, Dictionary<string, string> arguments = null, ResponseType responseType = ResponseType.Text,
            NameValueCollection headers = null, CookieCollection cookies = null)
        {
            HttpWebResponse response = null;

            try
            {
                if (method == HttpMethod.POST) // Multipart form data
                {
                    response = PostResponseMultiPart(url, arguments, headers, cookies);
                }
                else
                {
                    response = GetResponse(method, url, arguments, headers, cookies);
                }

                return ResponseToString(response, responseType);
            }
            finally
            {
                if (response != null)
                {
                    response.Close();
                }
            }
        }

        protected string SendRequest(HttpMethod method, string url, string content, Dictionary<string, string> arguments = null, ResponseType responseType = ResponseType.Text,
            NameValueCollection headers = null, CookieCollection cookies = null)
        {
            byte[] data = Encoding.UTF8.GetBytes(content);

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(data, 0, data.Length);
                return SendRequest(method, url, ms, arguments, responseType, headers, cookies);
            }
        }

        protected string SendRequest(HttpMethod method, string url, Stream content, Dictionary<string, string> arguments = null, ResponseType responseType = ResponseType.Text,
            NameValueCollection headers = null, CookieCollection cookies = null)
        {
            using (HttpWebResponse response = GetResponse(method, url, arguments, headers, cookies, content))
            {
                return ResponseToString(response, responseType);
            }
        }

        protected bool SendRequest(HttpMethod method, Stream downloadStream, string url, Dictionary<string, string> arguments = null,
            NameValueCollection headers = null, CookieCollection cookies = null)
        {
            using (HttpWebResponse response = GetResponse(method, url, arguments, headers, cookies))
            {
                if (response != null)
                {
                    response.GetResponseStream().CopyStreamTo(downloadStream, BufferSize);
                    return true;
                }
            }

            return false;
        }

        private HttpWebResponse GetResponse(HttpMethod method, string url, Dictionary<string, string> arguments = null,
            NameValueCollection headers = null, CookieCollection cookies = null, Stream dataStream = null)
        {
            IsUploading = true;
            stopUpload = false;

            url = CreateQuery(url, arguments);

            try
            {
                HttpWebRequest request = PrepareWebRequest(method, url, headers, cookies);

                if (dataStream != null)
                {
                    using (Stream requestStream = request.GetRequestStream())
                    {
                        if (!TransferData(dataStream, requestStream))
                        {
                            return null;
                        }
                    }
                }

                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException e)
            {
                if (SuppressWebExceptions) throw;
                if (!stopUpload) AddWebError(e);
            }
            catch (Exception e)
            {
                if (!stopUpload) AddWebError(e);
            }
            finally
            {
                IsUploading = false;
            }

            return null;
        }

        #endregion Request methods

        #region Post methods

        protected string SendPostRequestJSON(string url, string json, ResponseType responseType = ResponseType.Text, CookieCollection cookies = null, NameValueCollection headers = null)
        {
            using (HttpWebResponse response = PostResponseJSON(url, json, cookies, headers))
            {
                return ResponseToString(response, responseType);
            }
        }

        protected string SendPostRequestStream(string url, Stream stream, string contentType, CookieCollection cookies = null, NameValueCollection headers = null)
        {
            using (HttpWebResponse response = GetResponseUsingPost(url, stream, CreateBoundary(), contentType, cookies, headers))
            {
                return ResponseToString(response);
            }
        }

        private HttpWebResponse PostResponseMultiPart(string url, Dictionary<string, string> arguments, NameValueCollection headers = null, CookieCollection cookies = null)
        {
            string boundary = CreateBoundary();
            byte[] data = MakeInputContent(boundary, arguments);

            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(data, 0, data.Length);
                return GetResponseUsingPost(url, stream, boundary, "multipart/form-data", cookies, headers);
            }
        }

        private HttpWebResponse PostResponseJSON(string url, string json, CookieCollection cookies = null, NameValueCollection headers = null)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);

            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(data, 0, data.Length);
                return GetResponseUsingPost(url, stream, null, "application/json", cookies, headers);
            }
        }

        private HttpWebResponse GetResponseUsingPost(string url, Stream dataStream, string boundary, string contentType,
            CookieCollection cookies = null, NameValueCollection headers = null)
        {
            IsUploading = true;
            stopUpload = false;

            try
            {
                HttpWebRequest request = PreparePostWebRequest(url, boundary, dataStream.Length, contentType, cookies, headers);

                using (Stream requestStream = request.GetRequestStream())
                {
                    if (!TransferData(dataStream, requestStream)) return null;
                }

                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException e)
            {
                if (SuppressWebExceptions) throw;
                if (!stopUpload) AddWebError(e);
            }
            catch (Exception e)
            {
                if (!stopUpload) AddWebError(e);
            }
            finally
            {
                IsUploading = false;
            }

            return null;
        }

        protected UploadResult UploadData(Stream dataStream, string url, string fileName, string fileFormName = "file",
            Dictionary<string, string> arguments = null, CookieCollection cookies = null, NameValueCollection headers = null,
            ResponseType responseType = ResponseType.Text)
        {
            UploadResult result = new UploadResult();

            IsUploading = true;
            stopUpload = false;

            try
            {
                string boundary = CreateBoundary();

                byte[] bytesArguments = MakeInputContent(boundary, arguments, false);
                byte[] bytesDataOpen = MakeFileInputContentOpen(boundary, fileFormName, fileName);
                byte[] bytesDataClose = MakeFileInputContentClose(boundary);

                long contentLength = bytesArguments.Length + bytesDataOpen.Length + dataStream.Length + bytesDataClose.Length;
                HttpWebRequest request = PreparePostWebRequest(url, boundary, contentLength, "multipart/form-data", cookies, headers);

                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(bytesArguments, 0, bytesArguments.Length);
                    requestStream.Write(bytesDataOpen, 0, bytesDataOpen.Length);
                    if (!TransferData(dataStream, requestStream)) return null;
                    requestStream.Write(bytesDataClose, 0, bytesDataClose.Length);
                }

                result.Response = ResponseToString(request.GetResponse(), responseType);
                result.IsSuccess = true;
            }
            catch (WebException e)
            {
                if (SuppressWebExceptions) throw;
                if (!stopUpload) result.Response = AddWebError(e);
            }
            catch (Exception e)
            {
                if (!stopUpload) result.Response = AddWebError(e);
            }
            finally
            {
                IsUploading = false;
            }

            return result;
        }

        #endregion Post methods

        #region Helper methods

        private HttpWebRequest PreparePostWebRequest(string url, string boundary, long length, string contentType, CookieCollection cookies = null,
            NameValueCollection headers = null)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            if (headers != null && headers["Accept"] != null)
            {
                request.Accept = headers["Accept"];
                headers.Remove("Accept");
            }

            request.AllowWriteStreamBuffering = ProxyInfo.Current.IsValidProxy();
            request.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);
            request.ContentLength = length;
            if (!string.IsNullOrEmpty(boundary)) contentType += "; boundary=" + boundary;
            request.ContentType = contentType;
            request.CookieContainer = new CookieContainer();
            if (cookies != null) request.CookieContainer.Add(cookies);
            if (headers != null) request.Headers.Add(headers);
            request.KeepAlive = false;
            request.Method = HttpMethod.POST.ToString();
            request.Pipelined = false;
            request.ProtocolVersion = HttpVersion.Version11;
            request.Proxy = ProxyInfo.Current.GetWebProxy();
            request.Timeout = -1;
            request.UserAgent = UserAgent;

            return request;
        }

        private HttpWebRequest PrepareWebRequest(HttpMethod method, string url, NameValueCollection headers = null, CookieCollection cookies = null)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method.ToString();
            if (headers != null) request.Headers.Add(headers);
            request.CookieContainer = new CookieContainer();
            if (cookies != null) request.CookieContainer.Add(cookies);
            request.KeepAlive = false;
            IWebProxy proxy = ProxyInfo.Current.GetWebProxy();
            if (proxy != null) request.Proxy = proxy;
            request.UserAgent = UserAgent;

            return request;
        }

        protected bool TransferData(Stream dataStream, Stream requestStream)
        {
            if (dataStream.CanSeek)
            {
                dataStream.Position = 0;
            }

            ProgressManager progress = new ProgressManager(dataStream.Length);
            int length = (int)Math.Min(BufferSize, dataStream.Length);
            byte[] buffer = new byte[length];
            int bytesRead;

            while (!stopUpload && (bytesRead = dataStream.Read(buffer, 0, length)) > 0)
            {
                requestStream.Write(buffer, 0, bytesRead);

                if (AllowReportProgress && progress.UpdateProgress(bytesRead))
                {
                    OnProgressChanged(progress);
                }
            }

            return !stopUpload;
        }

        private string CreateBoundary()
        {
            return new string('-', 20) + FastDateTime.Now.Ticks.ToString("x");
        }

        private byte[] MakeInputContent(string boundary, string name, string value)
        {
            string format = string.Format("--{0}\r\nContent-Disposition: form-data; name=\"{1}\"\r\n\r\n{2}\r\n", boundary, name, value);
            return Encoding.UTF8.GetBytes(format);
        }

        private byte[] MakeInputContent(string boundary, Dictionary<string, string> contents, bool isFinal = true)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                if (string.IsNullOrEmpty(boundary)) boundary = CreateBoundary();
                byte[] bytes;

                if (contents != null)
                {
                    foreach (KeyValuePair<string, string> content in contents)
                    {
                        if (!string.IsNullOrEmpty(content.Key) && !string.IsNullOrEmpty(content.Value))
                        {
                            bytes = MakeInputContent(boundary, content.Key, content.Value);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    }

                    if (isFinal)
                    {
                        bytes = MakeFinalBoundary(boundary);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }

                return stream.ToArray();
            }
        }

        private byte[] MakeFileInputContentOpen(string boundary, string fileFormName, string fileName)
        {
            string format = string.Format("--{0}\r\nContent-Disposition: form-data; name=\"{1}\"; filename=\"{2}\"\r\nContent-Type: {3}\r\n\r\n",
                boundary, fileFormName, fileName, Helpers.GetMimeType(fileName));
            return Encoding.UTF8.GetBytes(format);
        }

        private byte[] MakeFileInputContentClose(string boundary)
        {
            return Encoding.UTF8.GetBytes(string.Format("\r\n--{0}--\r\n", boundary));
        }

        private byte[] MakeFinalBoundary(string boundary)
        {
            return Encoding.UTF8.GetBytes(string.Format("--{0}--\r\n", boundary));
        }

        private string ResponseToString(WebResponse response, ResponseType responseType = ResponseType.Text)
        {
            if (response != null)
            {
                using (response)
                {
                    switch (responseType)
                    {
                        case ResponseType.Text:
                            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                            {
                                return reader.ReadToEnd();
                            }
                        case ResponseType.RedirectionURL:
                            return response.ResponseUri.OriginalString;
                        case ResponseType.Headers:
                            StringBuilder sbHeaders = new StringBuilder();
                            foreach (string key in response.Headers.AllKeys)
                            {
                                string value = response.Headers[key];
                                sbHeaders.AppendFormat("{0}: \"{1}\"{2}", key, value, Environment.NewLine);
                            }
                            return sbHeaders.ToString().Trim();
                    }
                }
            }

            return null;
        }

        protected string CreateQuery(Dictionary<string, string> args)
        {
            if (args != null && args.Count > 0)
            {
                return string.Join("&", args.Select(x => x.Key + "=" + HttpUtility.UrlEncode(x.Value)).ToArray());
            }

            return string.Empty;
        }

        protected string CreateQuery(string url, Dictionary<string, string> args)
        {
            string query = CreateQuery(args);

            if (!string.IsNullOrEmpty(query))
            {
                return url + "?" + query;
            }

            return url;
        }

        protected string CreateQuery(NameValueCollection args)
        {
            if (args != null && args.Count > 0)
            {
                List<string> commands = new List<string>();

                foreach (string key in args.AllKeys)
                {
                    string[] values = args.GetValues(key);
                    string isArray = values.Length > 1 ? "[]" : string.Empty;

                    commands.AddRange(values.Select(value => key + isArray + "=" + HttpUtility.UrlEncode(value)));
                }

                return string.Join("&", commands.ToArray());
            }

            return string.Empty;
        }

        protected string CreateQuery(string url, NameValueCollection args)
        {
            string query = CreateQuery(args);

            if (!string.IsNullOrEmpty(query))
            {
                return url + "?" + query;
            }

            return url;
        }

        protected NameValueCollection CreateAuthenticationHeader(string username, string password)
        {
            string authInfo = username + ":" + password;
            authInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(authInfo));
            NameValueCollection headers = new NameValueCollection();
            headers["Authorization"] = "Basic " + authInfo;
            return headers;
        }

        private string AddWebError(Exception e)
        {
            string response = null;

            if (Errors != null && e != null)
            {
                StringBuilder str = new StringBuilder();
                str.AppendLine("Message:");
                str.AppendLine(e.Message);

                if (e is WebException)
                {
                    try
                    {
                        response = ResponseToString(((WebException)e).Response);

                        if (!string.IsNullOrEmpty(response))
                        {
                            str.AppendLine();
                            str.AppendLine("Response:");
                            str.AppendLine(response);
                        }
                    }
                    catch
                    {
                    }
                }

                str.AppendLine();
                str.AppendLine("StackTrace:");
                str.AppendLine(e.StackTrace);

                string errorText = str.ToString().Trim();
                Errors.Add(errorText);
                DebugHelper.WriteLine("AddWebError(): " + errorText);
            }

            return response;
        }

        #endregion Helper methods

        #region OAuth methods

        protected string GetAuthorizationURL(string requestTokenURL, string authorizeURL, OAuthInfo authInfo,
            Dictionary<string, string> customParameters = null, HttpMethod httpMethod = HttpMethod.GET)
        {
            string url = OAuthManager.GenerateQuery(requestTokenURL, customParameters, httpMethod, authInfo);

            string response = SendRequest(httpMethod, url);

            if (!string.IsNullOrEmpty(response))
            {
                return OAuthManager.GetAuthorizationURL(response, authInfo, authorizeURL);
            }

            return null;
        }

        protected bool GetAccessToken(string accessTokenURL, OAuthInfo authInfo, HttpMethod httpMethod = HttpMethod.GET)
        {
            return GetAccessTokenEx(accessTokenURL, authInfo, httpMethod) != null;
        }

        protected NameValueCollection GetAccessTokenEx(string accessTokenURL, OAuthInfo authInfo, HttpMethod httpMethod = HttpMethod.GET)
        {
            if (string.IsNullOrEmpty(authInfo.AuthToken) || string.IsNullOrEmpty(authInfo.AuthSecret))
            {
                throw new Exception("Auth infos missing. Open Authorization URL first.");
            }

            string url = OAuthManager.GenerateQuery(accessTokenURL, null, httpMethod, authInfo);

            string response = SendRequest(httpMethod, url);

            if (!string.IsNullOrEmpty(response))
            {
                return OAuthManager.ParseAccessTokenResponse(response, authInfo);
            }

            return null;
        }

        #endregion OAuth methods
    }
}