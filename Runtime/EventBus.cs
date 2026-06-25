using System;
using System.Collections.Generic;

namespace JulyEvents
{
    public sealed class EventBus : IEventBus
    {
        public static Action<Exception> ErrorHandler { get; set; }

        private interface IHandlerList
        {
            void Remove(Delegate handler);
            bool IsEmpty { get; }
        }

        private sealed class HandlerList<T> : IHandlerList
        {
            private readonly List<Action<T>> _list = new();
            private int _activeCount;
            private int _publishDepth;
            private bool _dirty;

            public bool Add(Action<T> handler)
            {
                if (_list.Contains(handler)) return false;
                _list.Add(handler);
                _activeCount++;
                return true;
            }

            public void Remove(Delegate handler)
            {
                var idx = _list.IndexOf((Action<T>)handler);
                if (idx < 0) return;
                _activeCount--;

                if (_publishDepth > 0)
                {
                    _list[idx] = null;
                    _dirty = true;
                }
                else
                {
                    _list.RemoveAt(idx);
                }
            }

            public bool IsEmpty => _activeCount <= 0;

            public void Publish(T eventData)
            {
                _publishDepth++;
                try
                {
                    var count = _list.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var h = _list[i];
                        if (h == null) continue;
                        try
                        {
                            h.Invoke(eventData);
                        }
                        catch (Exception e)
                        {
                            ErrorHandler?.Invoke(e);
                        }
                    }
                }
                finally
                {
                    _publishDepth--;
                    if (_publishDepth == 0 && _dirty)
                    {
                        _list.RemoveAll(static h => h == null);
                        _dirty = false;
                    }
                }
            }
        }

        private readonly Dictionary<Type, IHandlerList> _handlers = new();
        private readonly Dictionary<object, List<(Type type, Delegate handler)>> _ownerMap = new();
        private readonly Dictionary<Delegate, object> _handlerToOwner = new();
        private bool _disposed;

        public void Subscribe<T>(Action<T> handler, object owner)
        {
            if (_disposed) return;

            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                var typed = new HandlerList<T>();
                _handlers[type] = typed;
                list = typed;
            }

            if (!((HandlerList<T>)list).Add(handler)) return;

            if (!_ownerMap.TryGetValue(owner, out var ownerList))
            {
                ownerList = new List<(Type, Delegate)>();
                _ownerMap[owner] = ownerList;
            }

            ownerList.Add((type, handler));
            _handlerToOwner[handler] = owner;
        }

        public void Unsubscribe<T>(Action<T> handler)
        {
            if (_disposed) return;

            var type = typeof(T);
            if (_handlers.TryGetValue(type, out var list))
            {
                list.Remove(handler);
                if (list.IsEmpty)
                    _handlers.Remove(type);
            }

            RemoveFromOwnerMap(type, handler);
        }

        private void RemoveFromOwnerMap(Type eventType, Delegate handler)
        {
            if (!_handlerToOwner.TryGetValue(handler, out var owner)) return;
            _handlerToOwner.Remove(handler);

            if (!_ownerMap.TryGetValue(owner, out var ownerList)) return;

            for (int i = ownerList.Count - 1; i >= 0; i--)
            {
                if (ownerList[i].type == eventType && ownerList[i].handler == handler)
                {
                    ownerList.RemoveAt(i);
                    break;
                }
            }

            if (ownerList.Count == 0)
                _ownerMap.Remove(owner);
        }

        public void UnsubscribeAll(object owner)
        {
            if (_disposed) return;
            if (!_ownerMap.TryGetValue(owner, out var ownerList)) return;

            foreach (var (type, handler) in ownerList)
            {
                if (_handlers.TryGetValue(type, out var list))
                {
                    list.Remove(handler);
                    if (list.IsEmpty)
                        _handlers.Remove(type);
                }

                _handlerToOwner.Remove(handler);
            }

            _ownerMap.Remove(owner);
        }

        public void Publish<T>(T eventData)
        {
            if (_disposed) return;
            if (!_handlers.TryGetValue(typeof(T), out var list))
                return;

            ((HandlerList<T>)list).Publish(eventData);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handlers.Clear();
            _ownerMap.Clear();
            _handlerToOwner.Clear();
        }
    }
}
