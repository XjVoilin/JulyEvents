using System;

namespace JulyEvents
{
    public interface IEventBus : IDisposable
    {
        void Subscribe<T>(Action<T> handler, object owner);
        void Unsubscribe<T>(Action<T> handler);
        void UnsubscribeAll(object owner);
        void Publish<T>(T eventData);
    }
}
