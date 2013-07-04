﻿using System.Configuration;
using System.IO;
using System.Net.Sockets;
using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Http2HeadersCompression;
using Org.Mentalis;
using Org.Mentalis.Security.Ssl;
using Org.Mentalis.Security.Ssl.Shared.Extensions;
using SharedProtocol;
using SharedProtocol.Compression;
using SharedProtocol.Exceptions;
using SharedProtocol.ExtendedMath;
using SharedProtocol.Extensions;
using SharedProtocol.Framing;
using SharedProtocol.Handshake;
using SharedProtocol.Http11;
using SharedProtocol.IO;

namespace Client
{
    public sealed class Http2SessionHandler : IDisposable
    {
        private SecurityOptions _options;
        private Http2Session _clientSession;
        private const string _certificatePath = @"certificate.pfx";
        private Uri _requestUri;
        private SecureSocket _socket;
        private string _selectedProtocol;
        private bool _useHttp20 = true;
        private bool _usePriorities;
        private readonly bool _useHandshake;
        private readonly FileHelper _fileHelper;
        private readonly object _writeLock = new object();
        private const string _clientSessionHeader = @"FOO * HTTP/2.0\r\n\r\nBA\r\n\r\n";

        private static readonly string assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public bool IsHttp2WillBeUsed {
            get { return _useHttp20; }
        }

        public Http2SessionHandler(Uri requestUri, bool useHandshake, bool usePrioritization)
        {
            _usePriorities = usePrioritization;
            _useHandshake = useHandshake;
            _requestUri = requestUri;
            _fileHelper = new FileHelper();
        }

        public async void Connect()
        {
            if (_clientSession != null)
            {
                return;
            }

            SecureSocket sessionSocket = null;

            try
            {
                int port = _requestUri.Port;

                int securePort;

                try
                {
                    securePort = int.Parse(ConfigurationManager.AppSettings["securePort"]);
                }
                catch (Exception)
                {
                    Console.WriteLine("Incorrect port in the config file!");
                    return;
                }

                //Connect alpn extension, set known protocols
                var extensions = new[] {ExtensionType.Renegotiation, ExtensionType.ALPN};

                _options = port == securePort
                               ? new SecurityOptions(SecureProtocol.Tls1, extensions, new[] {"http/2.0", "http/1.1"},
                                                     ConnectionEnd.Client)
                               : new SecurityOptions(SecureProtocol.None, extensions, new[] {"http/2.0", "http/1.1"},
                                                     ConnectionEnd.Client);

                _options.VerificationType = CredentialVerification.None;
                _options.Certificate = Org.Mentalis.Security.Certificates.Certificate.CreateFromCerFile(_certificatePath);
                _options.Flags = SecurityFlags.Default;
                _options.AllowedAlgorithms = SslAlgorithms.RSA_AES_128_SHA | SslAlgorithms.NULL_COMPRESSION;

                sessionSocket = new SecureSocket(AddressFamily.InterNetwork, SocketType.Stream,
                                                 ProtocolType.Tcp, _options);

                using (var monitor = new ALPNExtensionMonitor())
                {
                    monitor.OnProtocolSelected += (o, args) => { _selectedProtocol = args.SelectedProtocol; };
                    sessionSocket.Connect(new DnsEndPoint(_requestUri.Host, _requestUri.Port), monitor);

                    if (_useHandshake)
                    {
                        //Handshake manager determines what handshake must be used: upgrade or secure
                        HandshakeManager.GetHandshakeAction(sessionSocket, _options).Invoke();

                        Console.WriteLine("Handshake finished");

                        if (_selectedProtocol == "http/1.1")
                        {
                            _useHttp20 = false;
                            return;
                        }
                    }
                }

                _socket = sessionSocket;
                SendSessionHeader();
                _useHttp20 = true;
                _clientSession = new Http2Session(_socket, ConnectionEnd.Client, _usePriorities);

                //For saving incoming data
                _clientSession.OnFrameReceived += FrameReceivedHandler;

                await _clientSession.Start();
            }
            catch (Http2HandshakeFailed)
            {
                _useHttp20 = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unhandled session exception was caught: " + ex.Message);
                Dispose();
            }
        }

        private void SendSessionHeader()
        {
            _socket.Send(Encoding.UTF8.GetBytes(_clientSessionHeader));
        }

        private void SubmitRequest(Uri request)
        {
            const string method = "get";
            string path = request.PathAndQuery;
            const string version = "http/2.0";
            string scheme = request.Scheme;
            string host = request.Host;

            var headers = new List<Tuple<string, string, IAdditionalHeaderInfo>>
                {
                    new Tuple<string, string, IAdditionalHeaderInfo>(":method", method, new Indexation(IndexationType.Indexed)),
                    new Tuple<string, string, IAdditionalHeaderInfo>(":path", path, new Indexation(IndexationType.Substitution)),
                    new Tuple<string, string, IAdditionalHeaderInfo>(":version", version, new Indexation(IndexationType.Incremental)),
                    new Tuple<string, string, IAdditionalHeaderInfo>(":host", host, new Indexation(IndexationType.Substitution)),
                    new Tuple<string, string, IAdditionalHeaderInfo>(":scheme", scheme, new Indexation(IndexationType.Substitution)),
                };

            //Sending request with average priority
            _clientSession.SendRequest(headers, (int)Priority.Pri3, false);
        }
        
        public void SendRequestAsync(Uri request)
        {
            if (_requestUri.Host != request.Host || _requestUri.Port != request.Port ||
                _requestUri.Scheme != request.Scheme)
            {
                throw new InvalidOperationException("Trying to send request to non connected address");
            }

            _requestUri = request;

            if (_useHttp20 == false)
            {
                Console.WriteLine("Download with Http/1.1");

                //Download with http11 in another thread.
                Task.Run(() => Http11Manager.Http11DownloadResource(_socket, _requestUri));
                return;
            }

            //Submit request if http2 was chosen
            Console.WriteLine("Submitting request");
            //Submit request in the current thread, responce will be handled in the session thread.
            SubmitRequest(_requestUri);
        }

        public TimeSpan Ping()
        {
            if (_clientSession != null)
            {
                return Task.Run(() => _clientSession.Ping()).Result;
            }

            return default(TimeSpan);
        }

        //Method for future usage in server push 
        private void SendResponce(Http2Stream stream)
        {
            byte[] binaryFile = _fileHelper.GetFile(stream.Headers.GetValue(":path"));
            int i = 0;

            Console.WriteLine("Transfer begin");

            while (binaryFile.Length > i)
            {
                bool isLastData = binaryFile.Length - i < Constants.MaxDataFrameContentSize;

                int chunkSize = stream.WindowSize > 0
                                ?
                                    MathEx.Min(binaryFile.Length - i, Constants.MaxDataFrameContentSize, stream.WindowSize)
                                :
                                    MathEx.Min(binaryFile.Length - i, Constants.MaxDataFrameContentSize);

                var chunk = new byte[chunkSize];
                Buffer.BlockCopy(binaryFile, i, chunk, 0, chunk.Length);

                stream.WriteDataFrame(chunk, isLastData);

                i += chunkSize;
            }

            //It was not send exactly. Some of the data frames could be pushed to the unshipped frames collection
            Console.WriteLine("File sent: " + stream.Headers.GetValue(":path"));
        }

        private void SaveToFile(Http2Stream stream, DataFrame dataFrame)
        {
            lock (_writeLock)
            {
                var path = stream.Headers.GetValue(":path");

                try
                {
                    if (dataFrame.Data.Count != 0)
                    {
                        _fileHelper.SaveToFile(dataFrame.Data.Array, dataFrame.Data.Offset, dataFrame.Data.Count,
                                               assemblyPath + path, stream.ReceivedDataAmount != 0);
                    }
                }
                catch (IOException)
                {
                    Console.WriteLine("File is still downloading. Repeat request later");
                    stream.WriteDataFrame(new byte[0], true);
                    stream.Dispose();
                }

                stream.ReceivedDataAmount += dataFrame.FrameLength;

                if (dataFrame.IsEndStream)
                {
                    if (!stream.EndStreamSent)
                    {
                        //send terminator
                        stream.WriteDataFrame(new byte[0], true);
                        Console.WriteLine("Terminator was sent");
                    }
                    _fileHelper.CloseStream(assemblyPath + path);
                }
            }
        }

        private void FrameReceivedHandler(object handler, FrameReceivedEventArgs args)
        {
            var stream = args.Stream;
            try
            {
                if (args.Frame is DataFrame)
                {
                    Task.Run(() => SaveToFile(stream, (DataFrame)args.Frame));
                }

                if (args.Frame is Headers)
                {
                    Task.Run(() => SendResponce(stream));
                }
            }
            catch (Exception)
            {
                stream.WriteRst(ResetStatusCode.InternalError);

                stream.Dispose();
            }
        }

        public void Dispose()
        {
            if (_clientSession != null)
            {
                _clientSession.Dispose();
            }
            else
            {
                _socket.Close();
            }
            _fileHelper.Dispose();
        }
    }
}
