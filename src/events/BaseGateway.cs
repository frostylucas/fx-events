﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Moonlight.Events.Diagnostics;
using Moonlight.Events.Exceptions;
using Moonlight.Events.Message;
using Moonlight.Events.Models;
using Moonlight.Events.Serialization;

namespace Moonlight.Events
{
    public delegate Task EventDelayMethod(int ms = 0);

    public delegate Task EventMessagePreparation(string pipeline, ISource source, IMessage message);

    public delegate void EventMessagePush(string pipeline, ISource source, byte[] buffer);

    [PublicAPI]
    public abstract class BaseGateway
    {
        protected abstract IEventLogger Logger { get; }
        protected abstract ISerialization Serialization { get; }

        private List<Tuple<EventMessage, EventHandler>> _processed =
            new List<Tuple<EventMessage, EventHandler>>();

        private List<EventObservable> _queue = new List<EventObservable>();
        private List<EventHandler> _handlers = new List<EventHandler>();

        public EventDelayMethod DelayDelegate { get; set; }
        public EventMessagePreparation PrepareDelegate { get; set; }
        public EventMessagePush PushDelegate { get; set; }

        public async Task ProcessInboundAsync(ISource source, byte[] serialized)
        {
            using var context =
                new SerializationContext(EventConstant.InboundPipeline, null, Serialization, serialized);
            var message = context.Deserialize<EventMessage>();

            await ProcessInboundAsync(message, source);
        }

        public async Task ProcessInboundAsync(EventMessage message, ISource source)
        {
            object InvokeDelegate(EventHandler subscription)
            {
                var parameters = new List<object>();
                var @delegate = subscription.Delegate;
                var method = @delegate.Method;
                var takesSource = method.GetParameters().Any(self => self.ParameterType == source.GetType());
                var startingIndex = takesSource ? 1 : 0;

                if (takesSource)
                {
                    parameters.Add(source);
                }

                if (message.Parameters == null) return @delegate.DynamicInvoke(parameters.ToArray());

                var array = message.Parameters.ToArray();
                var holder = new List<object>();
                var parameterInfos = @delegate.Method.GetParameters();

                for (var idx = 0; idx < array.Length; idx++)
                {
                    var parameter = array[idx];
                    var type = parameterInfos[startingIndex + idx].ParameterType;

                    using var context = new SerializationContext(message.Endpoint, $"(Out) Parameter Index {idx}", Serialization, parameter.Data);

                    holder.Add(context.Deserialize(type));
                }

                parameters.AddRange(holder.ToArray());

                return @delegate.DynamicInvoke(parameters.ToArray());
            }

            if (message.Flow == EventFlowType.Circle)
            {
                var stopwatch = StopwatchUtil.StartNew();
                var subscription = _handlers.SingleOrDefault(self => self.Endpoint == message.Endpoint) ??
                                   throw new Exception($"Could not find a handler for endpoint '{message.Endpoint}'");
                var result = InvokeDelegate(subscription);

                if (result?.GetType().GetGenericTypeDefinition() == typeof(Task<>))
                {
                    using var token = new CancellationTokenSource();

                    var task = (Task) result;
                    var timeout = Task.Run(async () => await DelayDelegate(10000), token.Token);
                    var completed = await Task.WhenAny(task, timeout);

                    if (completed == task)
                    {
                        token.Cancel();

                        await task.ConfigureAwait(false);

                        result = (object) ((dynamic) task).Result;
                    }
                    else
                    {
                        throw new EventTimeoutException(
                            $"({message.Endpoint} - {subscription.Delegate.Method.DeclaringType?.Name ?? "null"}/{subscription.Delegate.Method.Name}) The operation was timed out");
                    }
                }

                var resultType = result?.GetType() ?? typeof(object);
                var response = new EventResponseMessage(message.Id, message.Endpoint, message.Signature, null);

                using (var context = new SerializationContext(message.Endpoint, "Result", Serialization))
                {
                    context.Serialize(resultType, result);
                    response.Data = context.GetData();
                }

                using (var context = new SerializationContext(message.Endpoint, null, Serialization))
                {
                    context.Serialize(response);

                    var data = context.GetData();

                    PushDelegate(EventConstant.OutboundPipeline, source, data);
                    Logger.Debug(
                        $"[{message.Endpoint}] Responded to {source} with {data.Length} byte(s) in {stopwatch.Elapsed.TotalMilliseconds}ms");
                }
            }
            else
            {
                foreach (var handler in _handlers.Where(self => message.Endpoint == self.Endpoint))
                {
                    InvokeDelegate(handler);
                }
            }
        }

        public void ProcessOutbound(byte[] serialized)
        {
            using var context =
                new SerializationContext(EventConstant.OutboundPipeline, null, Serialization, serialized);
            var response = context.Deserialize<EventResponseMessage>();

            ProcessOutbound(response);
        }

        public void ProcessOutbound(EventResponseMessage response)
        {
            var waiting = _queue.SingleOrDefault(self => self.Message.Id == response.Id) ??
                          throw new Exception($"No request matching {response.Id} was found.");

            _queue.Remove(waiting);
            waiting.Callback.Invoke(response.Data);
        }

        protected async Task<EventMessage> SendInternal(EventFlowType flow, ISource source, string endpoint,
            params object[] args)
        {
            var stopwatch = StopwatchUtil.StartNew();
            var parameters = new List<EventParameter>();

            for (var idx = 0; idx < args.Length; idx++)
            {
                var argument = args[idx];
                var type = argument?.GetType() ?? typeof(object);

                using var context = new SerializationContext(endpoint, $"(In) Parameter Index '{idx}'",
                    Serialization);

                context.Serialize(type, argument);
                parameters.Add(new EventParameter(context.GetData()));
            }

            var message = new EventMessage(endpoint, flow, parameters);

            if (PrepareDelegate != null)
            {
                stopwatch.Stop();

                await PrepareDelegate(EventConstant.InboundPipeline, source, message);

                stopwatch.Start();
            }

            using (var context = new SerializationContext(endpoint, null, Serialization))
            {
                context.Serialize(message);

                var data = context.GetData();

                PushDelegate(EventConstant.InboundPipeline, source, data);
                Logger.Debug(
                    $"[{endpoint}] Sent {data.Length} byte(s) to {source} in {stopwatch.Elapsed.TotalMilliseconds}ms");

                return message;
            }
        }

        protected async Task<T> GetInternal<T>(ISource source, string endpoint, params object[] args)
        {
            var stopwatch = StopwatchUtil.StartNew();
            var message = await SendInternal(EventFlowType.Circle, source, endpoint, args);
            var token = new CancellationTokenSource();
            var holder = new EventValueHolder<T>();

            _queue.Add(new EventObservable(message, data =>
            {
                using var context = new SerializationContext(endpoint, "Response", Serialization, data);

                holder.Data = data;
                holder.Value = context.Deserialize<T>();

                token.Cancel();
            }));

            while (!token.IsCancellationRequested)
            {
                await DelayDelegate();
            }

            var elapsed = stopwatch.Elapsed.TotalMilliseconds;

            Logger.Debug(
                $"[{message.Endpoint}] Received response from {source} of {holder.Data.Length} byte(s) in {elapsed}ms");

            return holder.Value;
        }

        public void Mount(string endpoint, Delegate @delegate)
        {
            Logger.Debug($"Mounted: {endpoint}");
            _handlers.Add(new EventHandler(endpoint, @delegate));
        }
    }
}