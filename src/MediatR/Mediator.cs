using MediatR.NotificationPublishers;

namespace MediatR;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wrappers;

/// <summary>
/// Default mediator implementation relying on single- and multi instance delegates for resolving handlers.
/// </summary>
public class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly INotificationPublisher _publisher;
    private static readonly ConcurrentDictionary<Type, RequestHandlerBase> _requestHandlers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> _notificationHandlers = new();
    private static readonly ConcurrentDictionary<Type, StreamRequestHandlerBase> _streamRequestHandlers = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider. Can be a scoped or root provider</param>
    public Mediator(IServiceProvider serviceProvider) 
        : this(serviceProvider, new ForeachAwaitPublisher()) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider. Can be a scoped or root provider</param>
    /// <param name="publisher">Notification publisher. Defaults to <see cref="ForeachAwaitPublisher"/>.</param>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher)
    {
        _serviceProvider = serviceProvider;
        _publisher = publisher;
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = request.GetType();

        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlers.GetOrAdd(requestType,
            static t => (RequestHandlerBase)(Activator.CreateInstance(typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(t, typeof(TResponse)))
                                             ?? throw new InvalidOperationException($"Could not create wrapper type for {t}")));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = typeof(TRequest);

        var handler = (RequestHandlerWrapper)_requestHandlers.GetOrAdd(requestType,
            static t => (RequestHandlerBase)(Activator.CreateInstance(typeof(RequestHandlerWrapperImpl<>).MakeGenericType(t))
                                             ?? throw new InvalidOperationException($"Could not create wrapper type for {t}")));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var requestType = request.GetType();
        var handler = _requestHandlers.GetOrAdd(requestType,
            static requestTypeKey =>
            {
                var requestInterfaceType = requestTypeKey
                    .GetInterfaces()
                    .FirstOrDefault(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

                Type wrapperType;

                if (requestInterfaceType is null)
                {
                    requestInterfaceType = requestTypeKey
                        .GetInterfaces()
                        .FirstOrDefault(static i => i == typeof(IRequest));

                    if (requestInterfaceType is null)
                    {
                        throw new ArgumentException($"{requestTypeKey.Name} does not implement {nameof(IRequest)}",
                            nameof(request));
                    }

                    wrapperType =
                        typeof(RequestHandlerWrapperImpl<>).MakeGenericType(requestTypeKey);
                }
                else
                {
                    var responseType = requestInterfaceType.GetGenericArguments()[0];
                    wrapperType =
                        typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestTypeKey, responseType);
                }

                return (RequestHandlerBase)(Activator.CreateInstance(wrapperType) 
                                            ?? throw new InvalidOperationException($"Could not create wrapper for type {wrapperType}"));
            });

        // call via dynamic dispatch to avoid calling through reflection for performance reasons
        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        if (notification == null)
        {
            throw new ArgumentNullException(nameof(notification));
        }

        return PublishNotification(notification, cancellationToken);
    }

    public Task Publish(object notification, CancellationToken cancellationToken = default) =>
        notification switch
        {
            null => throw new ArgumentNullException(nameof(notification)),
            INotification instance => PublishNotification(instance, cancellationToken),
            _ => throw new ArgumentException($"{nameof(notification)} does not implement ${nameof(INotification)}")
        };

    /// <summary>
    /// Override in a derived class to control how the tasks are awaited. By default the implementation calls the <see cref="INotificationPublisher"/>.
    /// </summary>
    /// <param name="handlerExecutors">Enumerable of tasks representing invoking each notification handler</param>
    /// <param name="notification">The notification being published</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task representing invoking all handlers</returns>
    protected virtual Task PublishCore(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken) 
        => _publisher.Publish(handlerExecutors, notification, cancellationToken);

    private Task PublishNotification(INotification notification, CancellationToken cancellationToken = default)
    {
        var notificationType = notification.GetType();
        var handler = _notificationHandlers.GetOrAdd(notificationType,
            static t => (NotificationHandlerWrapper) (Activator.CreateInstance(typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t)) 
                                                      ?? throw new InvalidOperationException($"Could not create wrapper for type {t}")));

        return handler.Handle(notification, _serviceProvider, PublishCore, cancellationToken);
    }


    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = request.GetType();

        var streamHandler = (StreamRequestHandlerWrapper<TResponse>) _streamRequestHandlers.GetOrAdd(requestType,
            t => (StreamRequestHandlerBase) Activator.CreateInstance(typeof(StreamRequestHandlerWrapperImpl<,>).MakeGenericType(requestType, typeof(TResponse))));

        var items = streamHandler.Handle(request, _serviceProvider, cancellationToken);

        return items;
    }


    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = request.GetType();

        var handler = _streamRequestHandlers.GetOrAdd(requestType,
            requestTypeKey =>
            {
                var requestInterfaceType = requestTypeKey
                    .GetInterfaces()
                    .FirstOrDefault(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>));
                var isValidRequest = requestInterfaceType != null;

                if (!isValidRequest)
                {
                    throw new ArgumentException($"{requestType.Name} does not implement IStreamRequest<TResponse>", nameof(requestTypeKey));
                }

                var responseType = requestInterfaceType!.GetGenericArguments()[0];
                return (StreamRequestHandlerBase) Activator.CreateInstance(typeof(StreamRequestHandlerWrapperImpl<,>).MakeGenericType(requestTypeKey, responseType));
            });

        // call via dynamic dispatch to avoid calling through reflection for performance reasons
        var items = handler.Handle(request, _serviceProvider, cancellationToken);

        return items;
    }
}