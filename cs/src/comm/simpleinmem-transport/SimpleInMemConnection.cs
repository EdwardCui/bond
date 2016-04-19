﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.SimpleInMem
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.Comm.Service;

    public enum ConnectionType
    {
        Client,
        Server
    }

    public class SimpleInMemConnection : Connection, IRequestResponseConnection
    {
        private readonly int m_connection_delay = 20;
        private Guid m_connectionId;
        private ConnectionType m_connectionType;
        private ServiceHost m_serviceHost;
        private RequestResponseQueue m_clientreqresqueue;
        private RequestResponseQueueCollection m_serverqueues;
        private object m_requestsLock = new object();
        private long m_requestId;
        private CancellationTokenSource m_cancelTokenSource = new CancellationTokenSource();

        public SimpleInMemConnection(SimpleInMemTransport parentTransport, ConnectionType connectionType) : this (new ServiceHost(parentTransport), connectionType)
        {
        }

        internal SimpleInMemConnection(ServiceHost serviceHost, ConnectionType connectionType)
        {
            m_connectionId = Guid.NewGuid();
            m_connectionType = connectionType;
            m_serviceHost = serviceHost;

            if (connectionType == ConnectionType.Client)
            {
                m_clientreqresqueue = new RequestResponseQueue();
            }
            else if (connectionType == ConnectionType.Server)
            {
                m_serverqueues = new RequestResponseQueueCollection();
            }

            // start at -1 or 0 so the first request ID is 1 or 2.
            m_requestId = connectionType == ConnectionType.Client ? -1 : 0;
        }

        public override string ToString()
        {
            return $"{nameof(SimpleInMemConnection)}({m_connectionId})";
        }

        public override Task StopAsync()
        {
            m_cancelTokenSource.Cancel();
            return TaskExt.CompletedTask;
        }

        public async Task<IMessage<TResponse>> RequestResponseAsync<TRequest, TResponse>(string methodName, IMessage<TRequest> message, CancellationToken ct)
        {
            IMessage response = await SendRequestAsync(methodName, message);
            return response.Convert<TResponse>();
        }

        public ConnectionType ConnectionType
        {
            get
            {
                return m_connectionType;
            }
        }

        internal RequestResponseQueue RequestResponseQueue
        {
            get
            {
                return m_clientreqresqueue;
            }
        }

        internal Guid Id
        {
            get
            {
                return m_connectionId;
            }
        }

        internal Task<IMessage> SendRequestAsync(string methodName, IMessage request)
        {
            uint requestId = AllocateNextRequestId();
            var payload = NewPayLoad(requestId, PayloadType.Request, request, new TaskCompletionSource<IMessage>());
            payload.m_headers.method_name = methodName;
            m_clientreqresqueue.Enqueue(payload);

            return payload.m_outstandingRequest.Task;
        }


        internal void SendReplyAsync(uint requestId, IMessage response, RequestResponseQueue queue, TaskCompletionSource<IMessage> taskSource)
        {
            var payload = NewPayLoad(requestId, PayloadType.Response, response, taskSource);
            queue.Enqueue(payload);
        }

        protected UInt32 AllocateNextRequestId()
        {
            var requestIdLong = Interlocked.Add(ref m_requestId, 2);
            if (requestIdLong > UInt32.MaxValue)
            {
                var message = LogUtil.FatalAndReturnFormatted("{0}.{1}: Exhausted request IDs!",
                    this, nameof(AllocateNextRequestId));
                throw new SimpleInMemProtocolErrorException("Exhausted request IDs!");
            }

            return unchecked((UInt32)requestIdLong);
        }

        private InMemFrame NewPayLoad(uint requestId, PayloadType payloadType, IMessage message, TaskCompletionSource<IMessage> taskSource)
        {
            var headers = new SimpleInMemHeaders
            {
                request_id = requestId,
                payload_type = payloadType
            };

            return new InMemFrame
            {
                m_headers = headers,
                m_message = message,
                m_outstandingRequest = taskSource
            };
        }

        internal void Start()
        {
            if (m_connectionType == ConnectionType.Client)
            {
                Task.Run(() => ProcessResponseAsync(m_cancelTokenSource.Token));
            }
            else if (m_connectionType == ConnectionType.Server)
            {
                Task.Run(() => ProcessRequestAsync(m_cancelTokenSource.Token));
            }
            else
            {
                var message = LogUtil.FatalAndReturnFormatted("{0}.{1}: Connection type {2} not implemented.",
                    this, nameof(Start), m_connectionType);
                throw new NotImplementedException(message);
            }
        }

        internal void AddRequestResponseQueue(Guid id, RequestResponseQueue queue)
        {
            if (m_connectionType == ConnectionType.Client)
            {
                var message = LogUtil.FatalAndReturnFormatted(
                    "{0}.{1}: Client connection does not support adding new request response queue.",
                    this, nameof(AddRequestResponseQueue));
                throw new NotSupportedException(message);
            }

            m_serverqueues.AddRequestResponseQueue(id, queue);
        }

        private async Task ProcessResponseAsync(CancellationToken t)
        {
            while(!t.IsCancellationRequested)
            {
                PayloadType payloadType = PayloadType.Response;
                //connection delay
                await Task.Delay(m_connection_delay);

                if (m_clientreqresqueue.Count(payloadType) == 0)
                {
                    continue;
                }

                var frame = m_clientreqresqueue.Dequeue(payloadType);

                try
                {
                    Validate(frame);
                }
                catch (Exception e)
                {
                    Log.Error(e, "{0}.{1}: Exception while validating a frame: {2}", this, nameof(ProcessResponseAsync), e);
                    continue;
                }

                var headers = frame.m_headers;
                var message = frame.m_message;
                var taskSource = frame.m_outstandingRequest;

                await Task.Run(() => DispatchResponse(headers, message, taskSource));
            }
        }

        private async Task ProcessRequestAsync(CancellationToken t)
        {
            while (!t.IsCancellationRequested)
            {
                PayloadType payloadType = PayloadType.Request;
                //connection delay
                await Task.Delay(m_connection_delay);

                foreach (Guid key in m_serverqueues.GetKeys())
                {
                    RequestResponseQueue queue = m_serverqueues.GetQueue(key);

                    if (queue.Count(payloadType) == 0)
                    {
                        continue;
                    }

                    var payload = queue.Dequeue(payloadType);

                    try
                    {
                        Validate(payload);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "{0}.{1}: Exception while validating a frame: {2}", this, nameof(ProcessRequestAsync), e);
                        continue;
                    }

                    var headers = payload.m_headers;
                    var message = payload.m_message;
                    var taskSource = payload.m_outstandingRequest;

                    await Task.Run(() => DispatchRequest(headers, message, queue, taskSource));
                }
            }
        }

        private InMemFrame Validate(InMemFrame frame)
        {
            if (frame.m_headers == null)
            {
                throw new SimpleInMemProtocolErrorException("Missing headers");
            }
            else if (frame.m_message == null)
            {
                throw new SimpleInMemProtocolErrorException("Missing payload");
            }
            else if (PayloadType.Event == frame.m_headers.payload_type)
            {
                throw new NotImplementedException(frame.m_headers.payload_type.ToString());
            }

            return frame;
        }

        private async void DispatchRequest(SimpleInMemHeaders headers, IMessage message, RequestResponseQueue queue, TaskCompletionSource<IMessage> taskSource)
        {
            IMessage response = await m_serviceHost.DispatchRequest(headers.method_name, new SimpleInMemReceiveContext(this), message);
            SendReplyAsync(headers.request_id, response, queue, taskSource);
        }

        private void DispatchResponse(SimpleInMemHeaders headers, IMessage message, TaskCompletionSource<IMessage> responseCompletionSource)
        {
            responseCompletionSource.SetResult(message);
        }
    }
}
