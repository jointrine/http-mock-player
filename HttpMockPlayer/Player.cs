﻿using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Dynamic;

[assembly: InternalsVisibleTo("HttpMockPlayer.Tests")]

namespace HttpMockPlayer
{
    /// <summary>
    /// Serves as player and/or recorder of HTTP requests.
    /// </summary>
    public class Player
    {
        private Uri baseAddress, remoteAddress;
        private HttpListener httpListener;
        private Cassette cassette;
        private Record record;

        // mutex object, used to avoid collisions when processing 
        // an incoming request or updating the player state value
        private object statelock;

        /// <summary>
        /// Initializes a new instance of the <see cref="Player"/> class with base and remote address URI's.
        /// </summary>
        /// <param name="baseAddress">URI address on which play or record requests are accepted.</param>
        /// <param name="remoteAddress">URI address of the Internet resource being mocked.</param>
        /// <exception cref="ArgumentNullException"/>
        public Player(Uri baseAddress, Uri remoteAddress)
        {
            if (baseAddress == null)
            {
                throw new ArgumentNullException("baseAddress");
            }
            if (remoteAddress == null)
            {
                throw new ArgumentNullException("remoteAddress");
            }

            this.baseAddress = baseAddress;
            this.remoteAddress = remoteAddress;

            var baseAddressString = baseAddress.OriginalString;
            httpListener = new HttpListener();
            httpListener.Prefixes.Add(baseAddressString.EndsWith("/") ? baseAddressString : baseAddressString + "/");

            statelock = new object();
        }

        /// <summary>
        /// Gets URI address on which play or record requests are accepted.
        /// </summary>
        public Uri BaseAddress
        {
            get
            {
                return baseAddress;
            }
        }

        /// <summary>
        /// Gets or sets URI address of the Internet resource being mocked.
        /// </summary>
        /// <exception cref="ArgumentNullException"/>
        public Uri RemoteAddress
        {
            get
            {
                return remoteAddress;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }

                remoteAddress = value;
            }
        }

        /// <summary>
        /// Gets current state of this <see cref="Player"/> object.
        /// </summary>
        public State CurrentState { get; private set; }

        #region Mock request/response

        private enum PlayerErrorCode
        {
            RequestNotFound = 454,
            Exception = 550,
            PlayException = 551,
            RecordException = 552
        }

        private class MockRequest
        {
            private MockRequest() { }

            internal string Method { get; private set; }

            internal string Uri { get; private set; }

            internal string Content { get; private set; }

            internal NameValueCollection Headers { get; private set; }

            internal CookieCollection Cookies { get; private set; }

            internal JObject ToJson()
            {
                var jrequest = new JObject();

                jrequest.Add("method", Method);
                jrequest.Add("uri", Uri);

                if (Content != null)
                {
                    jrequest.Add("content", Content);
                }

                if (Headers != null)
                {
                    var jheaders = new JObject();
                    foreach (var header in Headers.AllKeys)
                    {
                        jheaders.Add(header, Headers[header]);
                    }
                    jrequest.Add("headers", jheaders);
                }

                if (Cookies != null)
                {
                    var jcookies = new JArray();
                    foreach (Cookie cookie in Cookies)
                    {
                        dynamic jcookie = new ExpandoObject();

                        jcookie.Name = cookie.Name;
                        jcookie.Value = cookie.Value;
                        jcookie.Domain = cookie.Domain;

                        if (!string.IsNullOrEmpty(cookie.Comment))
                        {
                            jcookie.Comment = cookie.Comment;
                        }
                        if (cookie.CommentUri != null)
                        {
                            jcookie.CommentUri = cookie.CommentUri;
                        }
                        if(cookie.Discard)
                        {
                            jcookie.Discard = cookie.Discard;
                        }
                        if (cookie.Expired)
                        {
                            jcookie.Expired = cookie.Expired;
                        }
                        if (cookie.Expires != DateTime.MinValue)
                        {
                            jcookie.Expires = cookie.Expires;
                        }
                        if (!string.IsNullOrEmpty(cookie.Path))
                        {
                            jcookie.Path = cookie.Path;
                        }
                        if (!string.IsNullOrEmpty(cookie.Port))
                        {
                            jcookie.Port = cookie.Port;
                        }
                        if (cookie.Secure)
                        {
                            jcookie.Secure = cookie.Secure;
                        }

                        jcookies.Add(JToken.FromObject(jcookie));
                    }
                    jrequest.Add("cookies", jcookies);
                }

                return jrequest;
            }

            internal static MockRequest FromJson(JObject jrequest)
            {
                var mockRequest = new MockRequest()
                {
                    Method = jrequest["method"].ToString(),
                    Uri = jrequest["uri"].ToString()
                };

                if (jrequest["content"] != null)
                {
                    mockRequest.Content = jrequest["content"].ToString();
                }

                if (jrequest["headers"] != null)
                {
                    mockRequest.Headers = new NameValueCollection();
                    foreach (JProperty jheader in jrequest["headers"])
                    {
                        mockRequest.Headers.Add(jheader.Name, jheader.Value.ToString());
                    }
                }

                if (jrequest["cookies"] != null)
                {
                    mockRequest.Cookies = new CookieCollection();
                    foreach (JObject jcookie in jrequest["cookies"])
                    {
                        mockRequest.Cookies.Add(jcookie.ToObject<Cookie>());
                    }
                }

                return mockRequest;
            }

            internal static MockRequest FromHttpRequest(Uri uri, HttpListenerRequest request)
            {
                var mockRequest = new MockRequest()
                {
                    Method = request.HttpMethod,
                    Uri = uri.OriginalString + request.Url.PathAndQuery
                };

                if (request.HasEntityBody)
                {
                    using (var stream = request.InputStream)
                    using (var reader = new StreamReader(stream, request.ContentEncoding))
                    {
                        mockRequest.Content = reader.ReadToEnd();
                    }
                }

                if(request.Headers != null && request.Headers.Count > 0)
                {
                    mockRequest.Headers = new NameValueCollection(request.Headers);

                    if (request.Headers["Host"] != null)
                    {
                        mockRequest.Headers["Host"] = uri.Authority;
                    }
                }

                if(request.Cookies != null && request.Cookies.Count > 0)
                {
                    mockRequest.Cookies = new CookieCollection { request.Cookies };

                    foreach (Cookie cookie in mockRequest.Cookies)
                    {
                        cookie.Domain = uri.Host;
                    }
                }

                return mockRequest;
            }

            private bool IsEqual(MockRequest mockRequest)
            {
                if (!string.Equals(Method, mockRequest.Method))
                {
                    return false;
                }

                if(!string.Equals(Uri, mockRequest.Uri))
                {
                    return false;
                }

                if(!string.Equals(Content, mockRequest.Content))
                {
                    return false;
                }

                NameValueCollection headers = null;

                // presence of Connection=Keep-Alive header is not persistent and depends on request order,
                // so it is skipped if there's no corresponding header in the live request
                if(Headers != null)
                {
                    headers = new NameValueCollection(Headers);

                    if (headers["Connection"] == "Keep-Alive" &&
                        mockRequest.Headers != null &&
                        mockRequest.Headers["Connection"] == null)
                    {
                        headers.Remove("Connection");
                    }
                }

                if((headers == null) != (mockRequest.Headers == null))
                {
                    return false;
                }
                if (headers != null)
                {
                    if (headers.Count != mockRequest.Headers.Count)
                    {
                        return false;
                    }
                    foreach (string header in headers)
                    {
                        if (!string.Equals(headers[header], mockRequest.Headers[header]))
                        {
                            return false;
                        }
                    }
                }

                if ((Cookies == null) != (mockRequest.Cookies == null))
                {
                    return false;
                }
                if(Cookies != null)
                {
                    if (Cookies.Count != mockRequest.Cookies.Count)
                    {
                        return false;
                    }
                    foreach (Cookie cookie in Cookies)
                    {
                        if (!cookie.Equals(mockRequest.Cookies[cookie.Name]))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                {
                    return false;
                }

                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                if (GetType() != obj.GetType())
                {
                    return false;
                }

                return IsEqual((MockRequest)obj);
            }

            public bool Equals(MockRequest mockRequest)
            {
                if (ReferenceEquals(null, mockRequest))
                {
                    return false;
                }

                if (ReferenceEquals(this, mockRequest))
                {
                    return true;
                }

                return IsEqual(mockRequest);
            }

            public override int GetHashCode()
            {
                return Tuple.Create(Method, Uri, Content).GetHashCode();
            }
        }

        private class MockResponse
        {
            private MockResponse() { }

            internal int StatusCode { get; private set; }

            internal string StatusDescription { get; private set; }

            internal string Content { get; private set; }

            internal WebHeaderCollection Headers { get; private set; }

            internal CookieCollection Cookies { get; private set; }

            internal JObject ToJson()
            {
                var jresponse = new JObject();

                jresponse.Add("statusCode", StatusCode);
                jresponse.Add("statusDescription", StatusDescription);

                if (Content != null)
                {
                    try
                    {
                        var jcontent = JToken.Parse(Content);
                        jresponse.Add("content", jcontent);
                    }
                    catch (JsonReaderException)
                    {
                        jresponse.Add("content", Content);
                    }
                }

                if (Headers != null)
                {
                    var jheaders = new JObject();
                    foreach (var header in Headers.AllKeys)
                    {
                        jheaders.Add(header, Headers[header]);
                    }
                    jresponse.Add("headers", jheaders);
                }

                if (Cookies != null)
                {
                    var jcookies = new JArray();
                    foreach (Cookie cookie in Cookies)
                    {
                        dynamic jcookie = new ExpandoObject();

                        jcookie.Name = cookie.Name;
                        jcookie.Value = cookie.Value;
                        jcookie.Domain = cookie.Domain;

                        if (!string.IsNullOrEmpty(cookie.Comment))
                        {
                            jcookie.Comment = cookie.Comment;
                        }
                        if (cookie.CommentUri != null)
                        {
                            jcookie.CommentUri = cookie.CommentUri;
                        }
                        if (cookie.Discard)
                        {
                            jcookie.Discard = cookie.Discard;
                        }
                        if (cookie.Expired)
                        {
                            jcookie.Expired = cookie.Expired;
                        }
                        if (cookie.Expires != DateTime.MinValue)
                        {
                            jcookie.Expires = cookie.Expires;
                        }
                        if (!string.IsNullOrEmpty(cookie.Path))
                        {
                            jcookie.Path = cookie.Path;
                        }
                        if (!string.IsNullOrEmpty(cookie.Port))
                        {
                            jcookie.Port = cookie.Port;
                        }
                        if (cookie.Secure)
                        {
                            jcookie.Secure = cookie.Secure;
                        }

                        jcookies.Add(JToken.FromObject(jcookie));

                    }
                    jresponse.Add("cookies", jcookies);
                }

                return jresponse;
            }

            internal static MockResponse FromJson(JObject jresponse)
            {
                var mockResponse = new MockResponse()
                {
                    StatusCode = jresponse["statusCode"].ToObject<int>(),
                    StatusDescription = jresponse["statusDescription"].ToString()
                };

                if (jresponse["content"] != null)
                {
                    mockResponse.Content = jresponse["content"].ToString();
                }

                if (jresponse["headers"] != null)
                {
                    mockResponse.Headers = new WebHeaderCollection();
                    foreach (JProperty jheader in jresponse["headers"])
                    {
                        mockResponse.Headers.Add(jheader.Name, jheader.Value.ToString());
                    }
                }

                if (jresponse["cookies"] != null)
                {
                    mockResponse.Cookies = new CookieCollection();
                    foreach (JObject jcookie in jresponse["cookies"])
                    {
                        mockResponse.Cookies.Add(jcookie.ToObject<Cookie>());
                    }
                }

                return mockResponse;
            }

            internal static MockResponse FromHttpResponse(HttpWebResponse response)
            {
                var mockResponse = new MockResponse()
                {
                    StatusCode = (int)response.StatusCode,
                    StatusDescription = response.StatusDescription
                };

                if (response.ContentLength > 0)
                {
                    using (var stream = response.GetResponseStream())
                    {
                        Encoding contentEncoding;
                        if (string.IsNullOrEmpty(response.ContentEncoding))
                        {
                            contentEncoding = Encoding.Default;
                        }
                        else
                        {
                            contentEncoding = Encoding.GetEncoding(response.ContentEncoding);
                        }

                        using (var reader = new StreamReader(stream, contentEncoding))
                        {
                            mockResponse.Content = reader.ReadToEnd();
                        }
                    }
                }

                if(response.Headers != null && response.Headers.Count > 0)
                {
                    mockResponse.Headers = response.Headers;
                }

                if(response.Cookies != null && response.Cookies.Count > 0)
                {
                    mockResponse.Cookies = response.Cookies;
                }

                return mockResponse;
            }

            internal static MockResponse FromPlayerError(PlayerErrorCode errorCode, string status, string message)
            {
                return new MockResponse()
                {
                    StatusCode = (int)errorCode,
                    StatusDescription = status,
                    Content = message
                };
            }
        }

        #endregion

        #region Http request/response

        private HttpWebRequest BuildRequest(MockRequest mockRequest)
        {
            var request = WebRequest.CreateHttp(new Uri(mockRequest.Uri));

            request.Method = mockRequest.Method;

            if (mockRequest.Headers != null)
            {
                foreach (string header in mockRequest.Headers)
                {
                    var value = mockRequest.Headers[header];

                    switch (header)
                    {
                        case "Accept":
                            request.Accept = value;
                            break;
                        case "Connection":
                            if (value.ToLower() == "keep-alive")
                            {
                                request.KeepAlive = true;
                            }
                            else if (value.ToLower() == "close")
                            {
                                request.KeepAlive = false;
                            }
                            else
                            {
                                request.Connection = value;
                            }
                            break;
                        case "Content-Length":
                            request.ContentLength = long.Parse(value);
                            break;
                        case "Content-Type":
                            request.ContentType = value;
                            break;
                        case "Date":
                            request.Date = DateTime.Parse(value);
                            break;
                        case "Expect":
                            var list = value.Split(',');
                            var values = list.Where(v => v != "100-continue");

                            value = string.Join(",", values);

                            if (!string.IsNullOrEmpty(value))
                            {
                                request.Expect = value;
                            }
                            break;
                        case "Host":
                            request.Host = value;
                            break;
                        case "If-Modified-Since":
                            request.IfModifiedSince = DateTime.Parse(value);
                            break;
                        case "Referer":
                            request.Referer = value;
                            break;
                        case "Transfer-Encoding":
                            if (value.ToLower() == "chunked")
                            {
                                request.SendChunked = true;
                            }
                            else
                            {
                                request.TransferEncoding = value;
                            }
                            break;
                        case "User-Agent":
                            request.UserAgent = value;
                            break;
                        default:
                            request.Headers[header] = value;
                            break;
                    }
                }
            }

            if (mockRequest.Cookies != null)
            {
                request.CookieContainer = new CookieContainer();
                request.CookieContainer.Add(mockRequest.Cookies);
            }

            if (mockRequest.Content != null)
            {
                var content = Encoding.Default.GetBytes(mockRequest.Content);

                request.ContentLength = content.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(content, 0, content.Length);
                }
            }

            return request;
        }

        private void BuildResponse(HttpListenerResponse response, MockResponse mockResponse)
        {
            response.StatusCode = mockResponse.StatusCode;
            response.StatusDescription = mockResponse.StatusDescription;

            response.Headers.Clear();

            if (mockResponse.Headers != null)
            {
                foreach (string header in mockResponse.Headers)
                {
                    var value = mockResponse.Headers[header];

                    switch (header)
                    {
                        case "Connection":
                            response.KeepAlive = (value.ToLower() == "keep-alive");
                            break;
                        case "Content-Length":
                            response.ContentLength64 = int.Parse(value);
                            break;
                        case "Content-Type":
                            response.ContentType = value;
                            break;
                        case "Location":
                            response.RedirectLocation = value;
                            break;
                        case "Transfer-Encoding":
                            response.SendChunked = (value.ToLower() == "chunked");
                            break;
                        default:
                            response.Headers[header] = value;
                            break;
                    }
                }
            }

            response.Cookies = mockResponse.Cookies;

            if (mockResponse.Content != null)
            {
                var content = Encoding.Default.GetBytes(mockResponse.Content);

                using (var stream = response.OutputStream)
                {
                    // writing to the output stream causes the response be submitted,
                    // i.e. not accepting any further property changes
                    stream.Write(content, 0, content.Length);
                }
            }
        }

        #endregion

        #region State

        /// <summary>
        /// Represents state of a <see cref="Player"/> object.
        /// </summary>
        public enum State
        {
            Off,
            Idle,
            Playing,
            Recording
        }

        /// <summary>
        /// Allows this instance to receive and process incoming requests.
        /// </summary>
        public void Start()
        {
            lock (statelock)
            {
                if (CurrentState != State.Off)
                {
                    throw new PlayerStateException(CurrentState, "Player has already started.");
                }

                httpListener.Start();

                Task.Run(() =>
                {
                    while (httpListener.IsListening)
                    {
                        var context = httpListener.GetContext();
                        var playerRequest = context.Request;
                        var playerResponse = context.Response;

                        lock (statelock)
                        {
                            try
                            {
                                switch (CurrentState)
                                {
                                    case State.Playing:
                                        {
                                            var mock = (JObject)record.Read();
                                            var mockRequest = MockRequest.FromJson((JObject)mock["request"]);
                                            var mockPlayerRequest = MockRequest.FromHttpRequest(remoteAddress, playerRequest);

                                            MockResponse mockResponse;

                                            if (mockRequest.Equals(mockPlayerRequest))
                                            {
                                                mockResponse = MockResponse.FromJson((JObject)mock["response"]);
                                            }
                                            else
                                            {
                                                mockResponse = MockResponse.FromPlayerError(PlayerErrorCode.RequestNotFound, "Player request mismatch", $"Player could not play the request at {playerRequest.Url.PathAndQuery}. The request doesn't match the current recorded one.");
                                            }

                                            BuildResponse(playerResponse, mockResponse);
                                        }

                                        break;
                                    case State.Recording:
                                        {
                                            var mockRequest = MockRequest.FromHttpRequest(remoteAddress, playerRequest);
                                            var request = BuildRequest(mockRequest);

                                            MockResponse mockResponse;

                                            try
                                            {
                                                using (var response = (HttpWebResponse)request.GetResponse())
                                                {
                                                    mockResponse = MockResponse.FromHttpResponse(response);
                                                }
                                            }
                                            catch (WebException ex)
                                            {
                                                if(ex.Response == null)
                                                {
                                                    throw ex;
                                                }

                                                mockResponse = MockResponse.FromHttpResponse((HttpWebResponse)ex.Response);
                                            }

                                            var mock = JObject.FromObject(new
                                            {
                                                request = mockRequest.ToJson(),
                                                response = mockResponse.ToJson()
                                            });
                                            record.Write(mock);

                                            BuildResponse(playerResponse, mockResponse);
                                        }

                                        break;
                                    default:
                                        throw new PlayerStateException(CurrentState, "Player is not in operation.");
                                }
                            }
                            catch (Exception ex)
                            {
                                PlayerErrorCode errorCode;
                                string process;

                                switch (CurrentState)
                                {
                                    case State.Playing:
                                        errorCode = PlayerErrorCode.PlayException;
                                        process = "play";

                                        break;
                                    case State.Recording:
                                        errorCode = PlayerErrorCode.RecordException;
                                        process = "record";

                                        break;
                                    default:
                                        errorCode = PlayerErrorCode.Exception;
                                        process = "process";

                                        break;
                                }

                                var mockResponse = MockResponse.FromPlayerError(errorCode, "Player exception", $"Player could not {process} the request at {playerRequest.Url.PathAndQuery} because of exception: {ex}");

                                BuildResponse(playerResponse, mockResponse);
                            }
                            finally
                            {
                                playerResponse.Close();
                            }
                        }
                    }
                });

                CurrentState = State.Idle;
            }
        }

        /// <summary>
        /// Sets this instance to <see cref="State.Playing"/> state and loads a mock record for replaying.
        /// </summary>
        /// <param name="name">Name of the record to replay.</param>
        /// <exception cref="PlayerStateException"/>
        /// <exception cref="InvalidOperationException"/>
        /// <exception cref="ArgumentException"/>
        public void Play(string name)
        {
            lock (statelock)
            {
                if (CurrentState == State.Off)
                {
                    throw new PlayerStateException(CurrentState, "Player is not started.");
                }

                if (CurrentState != State.Idle)
                {
                    throw new PlayerStateException(CurrentState, "Player is already in operation.");
                }

                if (cassette == null)
                {
                    throw new InvalidOperationException("Cassette is not loaded.");
                }

                record = cassette.Find(name);
                if (record == null)
                {
                    throw new ArgumentException($"Cassette doesn't contain a record with the given name: {name}.");
                }

                CurrentState = State.Playing;
            }
        }

        /// <summary>
        /// Sets this instance to <see cref="State.Recording"/> state and creates a new mock record for recording. 
        /// </summary>
        /// <param name="name">Name of the new record.</param>
        /// <exception cref="PlayerStateException"/>
        /// <exception cref="InvalidOperationException"/>
        public void Record(string name)
        {
            lock (statelock)
            {
                if (CurrentState == State.Off)
                {
                    throw new PlayerStateException(CurrentState, "Player is not started.");
                }

                if (CurrentState != State.Idle)
                {
                    throw new PlayerStateException(CurrentState, "Player is already in operation.");
                }

                if (cassette == null)
                {
                    throw new InvalidOperationException("Cassette is not loaded.");
                }

                record = new Record(name);

                CurrentState = State.Recording;
            }
        }

        /// <summary>
        /// Sets this instance to <see cref="State.Idle"/> state.
        /// </summary>
        public void Stop()
        {
            lock (statelock)
            {
                if (CurrentState == State.Off)
                {
                    throw new PlayerStateException(CurrentState, "Player is not started.");
                }

                if (CurrentState != State.Idle)
                {
                    record.Rewind();

                    if (CurrentState == State.Recording)
                    {
                        cassette.Save(record);
                    }

                    record = null;

                    CurrentState = State.Idle;
                }
            }
        }

        #endregion

        /// <summary>
        /// Loads a cassette to this <see cref="Player"/> object.
        /// </summary>
        /// <param name="cassette">Cassette to load.</param>
        public void Load(Cassette cassette)
        {
            this.cassette = cassette;
        }

        /// <summary>
        /// Shuts down this <see cref="Player"/> object.
        /// </summary>
        public void Close()
        {
            lock (statelock)
            {
                if (CurrentState != State.Off)
                {
                    if (CurrentState != State.Idle)
                    {
                        record.Rewind();

                        if (CurrentState == State.Recording)
                        {
                            cassette.Save(record);
                        }

                        record = null;
                    }

                    httpListener.Close();

                    CurrentState = State.Off;
                }
            }
        }
    }
}
