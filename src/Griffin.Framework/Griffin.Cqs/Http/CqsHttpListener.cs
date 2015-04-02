﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using DotNetCqs;
using Griffin.Core.External.SimpleJson;
using Griffin.Cqs.Authorization;
using Griffin.Cqs.Net;
using Griffin.Net.Channels;
using Griffin.Net.Protocols.Http;
using Griffin.Net.Protocols.Http.Authentication;
using HttpListener = Griffin.Net.Protocols.Http.HttpListener;

namespace Griffin.Cqs.Http
{
    /// <summary>
    ///     Receives CQS objects over HTTP, processes them and return replies.
    /// </summary>
    public class CqsHttpListener
    {
        private readonly Dictionary<string, Type> _cqsTypes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        private readonly HttpListener _listener = new HttpListener();
        private readonly CqsMessageProcessor _messageProcessor;
        private Action<string> _logger;
        private Func<ITcpChannel, HttpRequestBase, HttpResponseBase> _requestFilter;

        /// <summary>
        ///     Initializes a new instance of the <see cref="CqsHttpListener" /> class.
        /// </summary>
        /// <param name="messageProcessor">Used to execute the actual messages.</param>
        public CqsHttpListener(CqsMessageProcessor messageProcessor)
        {
            if (messageProcessor == null) throw new ArgumentNullException("messageProcessor");
            _messageProcessor = messageProcessor;
        }

        /// <summary>
        ///     Assign to get important log messages (typically errors)
        /// </summary>
        public Action<string> Logger
        {
            get { return _logger; }
            set
            {
                if (value == null)
                    _logger = s => { };
                else
                    _logger = value;
            }
        }

        /// <summary>
        ///     Assign to authenticate inbound requests.
        /// </summary>
        public IAuthenticator Authenticator { get; set; }

        /// <summary>
        ///     Use to filter inbound requests (or to perform authentication).
        /// </summary>
        /// <returns>
        ///     Response if you stopped the processing; otherwise <c>null</c> to allow this class to contine process the
        ///     inbound message.
        /// </returns>
        public Func<ITcpChannel, HttpRequestBase, HttpResponseBase> RequestFilter
        {
            get { return _requestFilter; }
            set
            {
                if (value == null)
                    _requestFilter = (x, y) => null;
                else
                    _requestFilter = value;
            }
        }

        /// <summary>
        ///     Assign if you want to use something else than <c>GenericPrincipal</c>.
        /// </summary>
        public IPrincipalFactory PrincipalFactory { get; set; }

        /// <summary>
        ///     Port that the listener is accepting new connections on.
        /// </summary>
        public int LocalPort
        {
            get { return _listener.LocalPort; }
        }

        /// <summary>
        ///     Map a type directly.
        /// </summary>
        /// <param name="type">Must implement one of the handler interfaces.</param>
        /// <exception cref="System.ArgumentNullException">type</exception>
        /// <exception cref="System.ArgumentException">
        ///     ' + type.FullName + ' do not implement one of the handler interfaces.
        ///     or
        ///     ' + type.FullName + ' is abstract or an interface.
        /// </exception>
        /// <exception cref="System.InvalidOperationException">
        ///     Duplicate mappings for a name (two different handlers may not have
        ///     the same class name).
        /// </exception>
        /// <remarks>
        ///     Required if the HTTP client do not supply the full .NET type name (just the class name of the command/query).
        /// </remarks>
        public void Map(Type type)
        {
            if (type == null) throw new ArgumentNullException("type");

            if (!IsCqsType(type))
                throw new ArgumentException("'" + type.FullName + "' is not a CQS object.");

            if (_cqsTypes.ContainsKey(type.Name))
                throw new InvalidOperationException(
                    string.Format("Duplicate mappings for name '{0}'. '{1}' and '{2}'.", type.Name, type.FullName,
                        _cqsTypes[type.Name].FullName));

            _cqsTypes.Add(type.Name, type);
        }

        /// <summary>
        ///     Scan assembly for handlers.
        /// </summary>
        /// <remarks>
        ///     Required if the HTTP client do not supply the full .NET type name (just the class name of the command/query).
        /// </remarks>
        /// <param name="assembly">The assembly to scan for handlers.</param>
        /// <exception cref="System.InvalidOperationException">
        ///     Duplicate mappings for a name (two different handlers may not have
        ///     the same class name).
        /// </exception>
        public void ScanAssembly(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!IsCqsType(type))
                    continue;

                if (_cqsTypes.ContainsKey(type.Name))
                    throw new InvalidOperationException(
                        string.Format("Duplicate mappings for name '{0}'. '{1}' and '{2}'.", type.Name, type.FullName,
                            _cqsTypes[type.Name].FullName));

                _cqsTypes.Add(type.Name, type);
            }
        }

        /// <summary>
        ///     Endpoint to listen on.
        /// </summary>
        /// <param name="endPoint">The end point.</param>
        public void Start(IPEndPoint endPoint)
        {
            _listener.Start(endPoint.Address, endPoint.Port);
            _listener.MessageReceived += OnMessage;
        }

        private bool AuthenticateUser(ITcpChannel channel, HttpRequestBase request)
        {
            if (channel.Data["Principal"] != null)
            {
                Thread.CurrentPrincipal = (IPrincipal) channel.Data["Principal"];
                return true;
            }

            try
            {
                var user = Authenticator.Authenticate(request);
                if (user == null)
                    return true;

                if (PrincipalFactory != null)
                {
                    var ctx = new PrincipalFactoryContext(request, user);
                    Thread.CurrentPrincipal = PrincipalFactory.Create(ctx);
                    channel.Data["Principal"] = Thread.CurrentPrincipal;
                    return true;
                }

                var roles = user as IUserWithRoles;
                if (roles == null)
                    throw new InvalidOperationException(
                        "You must specify a PrincipalFactory if you do not return a IUserWithRoles from your IAccountService.");

                Thread.CurrentPrincipal = new GenericPrincipal(new GenericIdentity(user.Username), roles.RoleNames);
                channel.Data["Principal"] = Thread.CurrentPrincipal;
            }
            catch (HttpException ex)
            {
                var response = request.CreateResponse();
                response.StatusCode = ex.HttpCode;
                response.ReasonPhrase = FirstLine(ex.Message);
                ;
                channel.Send(response);
                return false;
            }

            return true;
        }

        private string FirstLine(string msg)
        {
            var pos = msg.IndexOfAny(new[] {'\r', '\n'});
            if (pos == -1)
                return msg;

            return msg.Substring(0, pos);
        }

        /// <summary>
        ///     Determines whether the type implements the command handler interface
        /// </summary>
        /// <param name="cqsType">The type.</param>
        /// <returns><c>true</c> if the objects is a command handler; otherwise <c>false</c></returns>
        private static bool IsCqsType(Type cqsType)
        {
            if (cqsType.IsAbstract || cqsType.IsInterface)
                return false;

            if (cqsType.IsSubclassOf(typeof (Command))
                || cqsType.IsSubclassOf(typeof (ApplicationEvent)))
                return true;

            if (cqsType.BaseType == typeof (object) || cqsType.BaseType == null)
                return false;

            if (!cqsType.BaseType.IsGenericType)
                return false;

            var typeDef = cqsType.BaseType.GetGenericTypeDefinition();
            if (typeDef == typeof (Query<>)
                || typeDef == typeof (Request<>))
                return true;


            return false;
        }

        private void OnMessage(ITcpChannel channel, object message)
        {
            var request = (HttpRequestBase) message;

            if (_requestFilter != null)
            {
                var resp = _requestFilter(channel, request);
                if (resp != null)
                {
                    channel.Send(resp);
                    return;
                }
            }
            var name = request.Headers["X-Cqs-Type"];
            var dotNetType = request.Headers["X-Cqs-Object-Type"];
            var cqsName = request.Headers["X-Cqs-Name"];

            if (Authenticator != null)
            {
                if (!AuthenticateUser(channel, request))
                    return;
            }

            Type type;
            if (!string.IsNullOrEmpty(dotNetType))
            {
                type = Type.GetType(dotNetType);
                if (type == null)
                {
                    var response = request.CreateResponse();
                    response.StatusCode = 400;
                    response.ReasonPhrase = "Unknown type: " + dotNetType;
                    Logger("Unknown type: " + dotNetType + " for " + request.Uri);
                    channel.Send(response);
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(cqsName))
            {
                if (!_cqsTypes.TryGetValue(cqsName, out type))
                {
                    var response = request.CreateResponse();
                    response.StatusCode = 400;
                    response.ReasonPhrase = "Unknown type: " + cqsName;
                    Logger("Unknown type: " + cqsName + " for " + request.Uri);
                    channel.Send(response);
                    return;
                }
            }
            else
            {
                var response = request.CreateResponse();
                response.StatusCode = 400;
                response.ReasonPhrase =
                    "Expected a class name in the header 'X-Cqs-Name' or a .NET type name in the header 'X-Cqs-Object-Type'.";
                Logger(
                    "Expected a class name in the header 'X-Cqs-Name' or a .NET type name in the header 'X-Cqs-Object-Type' for " +
                    request.Uri);
                channel.Send(response);
                return;
            }


            string json = "{}";

            //empty json
            if (request.Body != null)
            {
                var reader = new StreamReader(request.Body);
                json = reader.ReadToEnd();
            }

            var cqs = SimpleJson.DeserializeObject(json, type);
            ClientResponse cqsReplyObject = null;

            Exception ex = null;
            try
            {
                cqsReplyObject = _messageProcessor.ProcessAsync(cqs).Result;
            }
            catch (AggregateException e1)
            {
                ex = e1.InnerException;
            }

            if (ex is HttpException)
            {
                var response = request.CreateResponse();
                response.StatusCode = ((HttpException)ex).HttpCode;
                response.ReasonPhrase = FirstLine(ex.Message);
                channel.Send(response);
                return;
            }
            else if (ex is AuthorizationException)
            {
                var authEx = (AuthorizationException) ex;
                var response = request.CreateResponse();
                response.StatusCode = 401;
                response.ReasonPhrase = FirstLine(ex.Message);
                channel.Send(response);
                return;
            }
            else if (ex != null)
            {
                var response = request.CreateResponse();
                response.StatusCode = 500;
                response.ReasonPhrase = FirstLine(ex.Message);
                channel.Send(response);
                return;
            }

            var reply = request.CreateResponse();
            reply.ContentType = "application/json";

            // for instance commands do not have a return value.
            if (cqsReplyObject.Body != null)
            {
                reply.AddHeader("X-Cqs-Object-Type", cqsReplyObject.Body.GetType().GetSimpleAssemblyQualifiedName());
                reply.AddHeader("X-Cqs-Name", cqsReplyObject.Body.GetType().Name);
                if (cqsReplyObject.Body is Exception)
                    reply.StatusCode = 500;

                json = SimpleJson.SerializeObject(cqsReplyObject.Body);
                var buffer = Encoding.UTF8.GetBytes(json);
                reply.Body = new MemoryStream();
                reply.Body.Write(buffer, 0, buffer.Length);
            }
            else
                reply.StatusCode = (int) HttpStatusCode.NoContent;

            channel.Send(reply);
        }
    }
}