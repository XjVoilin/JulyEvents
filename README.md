# JulyEvents

轻量级事件总线契约与默认实现（`com.july.events`）。提供统一的 `IEventBus` 接口，零外部依赖，纯 C# 程序集（`noEngineReferences: true`），可在 Unity 外独立使用。

> **本文档描述框架的真实行为，与 `Runtime/` 代码一一对应。**

## 核心机制

### `IEventBus` 接口

事件总线的唯一契约，4 个方法：

| 方法 | 说明 |
|------|------|
| `Subscribe<T>(Action<T> handler, object owner)` | 订阅事件，绑定 owner 用于批量注销 |
| `Unsubscribe<T>(Action<T> handler)` | 取消单个 handler 订阅 |
| `UnsubscribeAll(object owner)` | 按 owner 批量注销其所有订阅 |
| `Publish<T>(T eventData)` | 发布事件，分发给所有 handler |

`EventBus` 是默认实现，同时实现 `IDisposable`，Dispose 后所有操作静默忽略。

### Owner 追踪

每次 `Subscribe` 必须传入 `owner` 对象。框架内部维护 `_ownerMap` 与 `_handlerToOwner` 双向索引：

```
Subscribe(handler, owner)
  → 注册 handler 到 Type 级 handler 列表
  → 记录 (owner → [(type, handler), ...])

UnsubscribeAll(owner)
  → 遍历 owner 的全部订阅，逐个 Remove
  → 清理空列表与 owner 索引
```

JulyArch 的 `GameView` / `SystemBase` / `ProcedureBase` 在生命周期结束时调用 `UnsubscribeAll(this)`，业务侧通常无需手动逐个注销。

### 重入安全

Publish 过程中若 handler 内部调用 `Unsubscribe`，不会破坏正在迭代的列表：

```
Publish 开始 → _publishDepth++
  handler 内 Unsubscribe → 标记 slot 为 null + _dirty = true（不立即 RemoveAt）
Publish 结束 → _publishDepth--
  若 _publishDepth == 0 && _dirty → RemoveAll(null) 延迟清理
```

同一 handler 重复 Subscribe 会被去重（`Contains` 检查），不会触发两次。

### 异常隔离

单个 handler 抛异常不影响其余 handler 继续执行。异常通过静态回调上报：

```csharp
EventBus.ErrorHandler = ex => Debug.LogException(ex);
```

未设置 `ErrorHandler` 时异常被静默吞掉（handler 列表仍完整执行）。

## 使用示例

```csharp
using JulyEvents;

// 创建实例（JulyArch 中由 ArchContext 持有）
var bus = new EventBus();

// 定义事件（struct 或 class 均可）
public readonly struct ScoreChangedEvent
{
    public readonly int NewScore;
    public ScoreChangedEvent(int score) => NewScore = score;
}

// 订阅 — owner 用于批量注销
var view = this;
bus.Subscribe<ScoreChangedEvent>(OnScoreChanged, view);

void OnScoreChanged(ScoreChangedEvent e)
{
    Debug.Log($"Score: {e.NewScore}");
}

// 发布
bus.Publish(new ScoreChangedEvent(100));

// 注销
bus.Unsubscribe<ScoreChangedEvent>(OnScoreChanged);  // 单个
bus.UnsubscribeAll(view);                             // 批量
```

## 约定

| 约定 | 说明 |
|------|------|
| 事件类型用 struct | 值类型零 GC，推荐 `readonly struct` |
| Subscribe 必须传 owner | 框架靠 owner 做生命周期批量清理 |
| 不跨线程 Publish | 无锁设计，仅主线程安全 |
| 自定义实现可替换 | JulyArch 的 `ArchContext.Event` 接受任意 `IEventBus` |

## 依赖

无。程序集 `JulyEvents.Runtime`，`noEngineReferences: true`。
