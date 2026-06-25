using System;
using NUnit.Framework;

namespace JulyEvents.Tests
{
    /// <summary>
    /// EventBus 单元测试。
    ///
    /// 重点覆盖三块高风险逻辑：
    /// 1. 发布期间订阅增删（重入）—— HandlerList 用 _publishDepth + _dirty 延迟移除，避免遍历中改集合。
    /// 2. owner 批量注销 —— UnsubscribeAll(owner) 依据 _ownerMap 清掉该 owner 的全部订阅。
    /// 3. 单 handler 异常隔离 —— 一个 handler 抛异常不应打断同次发布的其他 handler。
    /// </summary>
    [TestFixture]
    public class EventBusTests
    {
        private EventBus _bus;

        [SetUp]
        public void SetUp()
        {
            // ErrorHandler 是静态字段，必须每个测试重置，否则跨用例污染。
            EventBus.ErrorHandler = null;
            _bus = new EventBus();
        }

        [TearDown]
        public void TearDown()
        {
            _bus.Dispose();
            EventBus.ErrorHandler = null;
        }

        #region 基本订阅/发布

        [Test]
        public void Subscribe_ThenPublish_HandlerInvoked()
        {
            int calls = 0;
            _bus.Subscribe<TestEvent>(e => calls++, this);

            _bus.Publish(new TestEvent());

            Assert.AreEqual(1, calls);
        }

        [Test]
        public void Publish_NoSubscribers_IsNoop()
        {
            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
        }

        [Test]
        public void Publish_PassesEventDataToHandler()
        {
            int received = 0;
            _bus.Subscribe<TestEvent>(e => received = e.Value, this);

            _bus.Publish(new TestEvent { Value = 42 });

            Assert.AreEqual(42, received);
        }

        #endregion

        #region 取消订阅

        [Test]
        public void Unsubscribe_StopsDelivery()
        {
            int calls = 0;
            Action<TestEvent> h = e => calls++;
            _bus.Subscribe(h, this);

            _bus.Unsubscribe(h);
            _bus.Publish(new TestEvent());

            Assert.AreEqual(0, calls);
        }

        [Test]
        public void Unsubscribe_DuplicateCall_IsSafe()
        {
            Action<TestEvent> h = e => { };
            _bus.Subscribe(h, this);
            _bus.Unsubscribe(h);

            Assert.DoesNotThrow(() => _bus.Unsubscribe(h));
        }

        [Test]
        public void Unsubscribe_NeverSubscribed_IsSafe()
        {
            Action<TestEvent> h = e => { };
            Assert.DoesNotThrow(() => _bus.Unsubscribe(h));
        }

        #endregion

        #region owner 追踪与 UnsubscribeAll

        [Test]
        public void UnsubscribeAll_RemovesAllHandlersForOwner()
        {
            int a = 0, b = 0;
            var owner = new object();
            _bus.Subscribe<TestEvent>(e => a++, owner);
            _bus.Subscribe<OtherEvent>(e => b++, owner);
            _bus.Subscribe<TestEvent>(e => a++, new object()); // 另一个 owner，不应被清掉

            _bus.UnsubscribeAll(owner);

            _bus.Publish(new TestEvent());
            _bus.Publish(new OtherEvent());

            Assert.AreEqual(1, a, "被批量注销的 owner 不应再收到 TestEvent；另一个 owner 的 handler 仍应触发 1 次");
            Assert.AreEqual(0, b, "被批量注销的 owner 不应再收到 OtherEvent");
        }

        [Test]
        public void UnsubscribeAll_NeverSubscribed_IsSafe()
        {
            Assert.DoesNotThrow(() => _bus.UnsubscribeAll(new object()));
        }

        [Test]
        public void SameHandlerSameOwner_SubscribedOnce()
        {
            int calls = 0;
            Action<TestEvent> h = e => calls++;
            _bus.Subscribe(h, this);
            _bus.Subscribe(h, this); // 重复订阅同一委托应被忽略

            _bus.Publish(new TestEvent());

            Assert.AreEqual(1, calls, "同一 handler 重复订阅只生效一次");
        }

        [Test]
        public void SameHandler_DifferentOwners_DeduplicatedByHandler()
        {
            int calls = 0;
            Action<TestEvent> h = e => calls++;
            _bus.Subscribe(h, new object());
            _bus.Subscribe(h, new object());

            _bus.Publish(new TestEvent());

            Assert.AreEqual(1, calls, "同一 handler 按委托身份去重，无论 owner 是否不同");
        }

        #endregion

        #region 重入：发布期间增删订阅

        [Test]
        public void Publish_DuringPublish_NewHandlerDoesNotReceiveCurrentBatch()
        {
            // 发布过程中新订阅的 handler 不应在本次发布中被调用（遍历已固定 count）。
            int received = 0;
            _bus.Subscribe<TestEvent>(e =>
            {
                _bus.Subscribe<TestEvent>(_ => received++, this);
            }, this);

            _bus.Publish(new TestEvent());
            Assert.AreEqual(0, received, "发布期新订阅的 handler 不应进入本次发布");

            // 但下一次发布会收到
            _bus.Publish(new TestEvent());
            Assert.AreEqual(1, received);
        }

        [Test]
        public void Publish_DuringPublish_UnsubscribeDoesNotThrow()
        {
            // 发布过程中注销自身/其他 handler，靠 _dirty 延迟移除，不应抛 InvalidOperationException。
            Action<TestEvent> other = e => { };
            _bus.Subscribe(other, this);

            _bus.Subscribe<TestEvent>(e =>
            {
                _bus.Unsubscribe(other); // 移除别的 handler
            }, this);

            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
        }

        [Test]
        public void Publish_DuringPublish_UnsubscribeSelf_SkipsRemaining()
        {
            // 订阅顺序：A(发布期注销 B) -> B -> C。
            // 发布期间 B 的槽位被置 null（_dirty 延迟物理移除），本轮遍历到 B 时 h==null 跳过；
            // C 不受影响仍被触发。验证重入期注销不抛异常、且被注销者本轮不再触发。
            bool bCalled = false, cCalled = false;
            Action<TestEvent> b = e => bCalled = true;
            Action<TestEvent> c = e => cCalled = true;

            _bus.Subscribe<TestEvent>(e => _bus.Unsubscribe(b), this);
            _bus.Subscribe(b, this);
            _bus.Subscribe(c, this);

            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
            Assert.IsFalse(bCalled, "B 在发布期被注销，本轮遍历到其槽位时应跳过");
            Assert.IsTrue(cCalled, "C 不应受 B 被注销影响");
        }

        [Test]
        public void Publish_DuringPublish_ReentrantPublish_Supported()
        {
            // handler 内部再次 Publish 同一事件 —— _publishDepth 计数递增，不应破坏清理。
            int depth = 0;
            int maxDepth = 0;
            _bus.Subscribe<TestEvent>(e =>
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
                if (depth < 3)
                    _bus.Publish(new TestEvent());
                depth--;
            }, this);

            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
            Assert.AreEqual(3, maxDepth, "应支持嵌套发布");
        }

        #endregion

        #region 异常隔离

        [Test]
        public void Publish_HandlerThrows_DoesNotStopOthers()
        {
            bool secondCalled = false;
            _bus.Subscribe<TestEvent>(e => throw new InvalidOperationException("boom"), this);
            _bus.Subscribe<TestEvent>(e => secondCalled = true, this);

            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
            Assert.IsTrue(secondCalled, "前一个 handler 抛异常不应打断后续 handler");
        }

        [Test]
        public void Publish_HandlerThrows_RoutesToErrorHandler()
        {
            Exception caught = null;
            EventBus.ErrorHandler = ex => caught = ex;

            var expected = new InvalidOperationException("boom");
            _bus.Subscribe<TestEvent>(e => throw expected, this);

            _bus.Publish(new TestEvent());

            Assert.AreSame(expected, caught, "handler 抛出的异常应路由到 ErrorHandler");
        }

        [Test]
        public void Publish_NoErrorHandler_HandlerThrowsSwallowed()
        {
            EventBus.ErrorHandler = null;
            _bus.Subscribe<TestEvent>(e => throw new InvalidOperationException("boom"), this);

            // 没有 ErrorHandler 时异常被吞掉，发布本身不应抛。
            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
        }

        #endregion

        #region Dispose

        [Test]
        public void Dispose_ThenSubscribe_IsNoop()
        {
            int calls = 0;
            _bus.Dispose();

            _bus.Subscribe<TestEvent>(e => calls++, this);
            _bus.Publish(new TestEvent());

            Assert.AreEqual(0, calls, "Dispose 后订阅与发布都应是空操作");
        }

        [Test]
        public void Dispose_ThenPublish_IsNoop()
        {
            _bus.Subscribe<TestEvent>(e => { }, this);
            _bus.Dispose();

            Assert.DoesNotThrow(() => _bus.Publish(new TestEvent()));
        }

        #endregion

        #region Stubs

        private struct TestEvent { public int Value; }

        private struct OtherEvent { }

        #endregion
    }
}
