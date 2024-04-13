﻿#nullable enable
using Fluxor.Persistence;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
#if NET6_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Nodes;
#endif
using System.Threading.Tasks;
using UnhandledExceptionEventArgs = Fluxor.Exceptions.UnhandledExceptionEventArgs;

namespace Fluxor;

/// <see cref="IStore"/>
public class Store : IStore, IActionSubscriber, IDisposable
{
	/// <see cref="IStore.Features"/>
	public IReadOnlyDictionary<string, IFeature> Features => FeaturesByName;
	/// <see cref="IStore.Initialized"/>
	public Task Initialized => InitializedCompletionSource.Task;

#if NET6_0_OR_GREATER
		private readonly IPersistenceManager? PersistenceManager;
#endif

	private object SyncRoot = new object();
	private bool Disposed;
	private readonly IDispatcher Dispatcher;
	private readonly Dictionary<string, IFeature> FeaturesByName = new(StringComparer.InvariantCultureIgnoreCase);
	private readonly List<IEffect> Effects = new();
	private readonly List<IMiddleware> Middlewares = new();
	private readonly List<IMiddleware> ReversedMiddlewares = new();
	private readonly ConcurrentQueue<object> QueuedActions = new();
	private readonly TaskCompletionSource<bool> InitializedCompletionSource = new();
	private readonly ActionSubscriber ActionSubscriber;

	private volatile bool IsDispatching;
	private volatile int BeginMiddlewareChangeCount;
	private volatile bool HasActivatedStore;
	private bool IsInsideMiddlewareChange => BeginMiddlewareChangeCount > 0;

#if NET6_0_OR_GREATER

	/// <summary>
	/// Creates an instance of the store
	/// </summary>
		public Store(IDispatcher dispatcher, IPersistenceManager? persistenceManager = null)
		{
			PersistenceManager = persistenceManager;
			ActionSubscriber = new ActionSubscriber();
			Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
			Dispatcher.ActionDispatched += ActionDispatched!;
			Dispatcher.Dispatch(new StoreInitializedAction());
		}

#else

		/// <summary>
		/// Creates an instance of the store
		/// </summary>
	public Store(IDispatcher dispatcher)
	{

		ActionSubscriber = new ActionSubscriber();
		Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		Dispatcher.ActionDispatched += ActionDispatched;
		Dispatcher.Dispatch(new StoreInitializedAction());
	}
#endif

	/// <see cref="IStore.GetMiddlewares"/>
	public IEnumerable<IMiddleware> GetMiddlewares() => Middlewares;

	/// <see cref="IStore.AddFeature(IFeature)"/>
	public void AddFeature(IFeature feature)
	{
		if (feature is null)
			throw new ArgumentNullException(nameof(feature));

		lock (SyncRoot)
		{
			FeaturesByName.Add(feature.GetName(), feature);
		}
	}

	/// <see cref="IStore.AddEffect(IEffect)"/>
	public void AddEffect(IEffect effect)
	{
		if (effect is null)
			throw new ArgumentNullException(nameof(effect));

		lock (SyncRoot)
		{
			Effects.Add(effect);
		}
	}

	/// <see cref="IStore.AddMiddleware(IMiddleware)"/>
	public void AddMiddleware(IMiddleware middleware)
	{
		lock (SyncRoot)
		{
			Middlewares.Add(middleware);
			ReversedMiddlewares.Insert(0, middleware);
			// Initialize the middleware immediately if the store has already been initialized, otherwise this will be
			// done the first time Dispatch is called
			if (HasActivatedStore)
			{
				middleware
					.InitializeAsync(Dispatcher, this)
					.ContinueWith(t =>
					{
						if (!t.IsFaulted)
							middleware.AfterInitializeAllMiddlewares();
					});
			}
		}
	}

	/// <see cref="IStore.BeginInternalMiddlewareChange"/>
	public IDisposable BeginInternalMiddlewareChange()
	{
		IDisposable[]? disposables;
		lock (SyncRoot)
		{
			BeginMiddlewareChangeCount++;
			disposables = Middlewares
				.Select(x => x.BeginInternalMiddlewareChange())
				.ToArray();
		}

		return new DisposableCallback(
			id: $"{nameof(Store)}.{nameof(BeginInternalMiddlewareChange)}",
			() => EndMiddlewareChange(disposables));
	}

	/// <see cref="IStore.InitializeAsync"/>
	public async Task InitializeAsync()
	{
		if (HasActivatedStore)
			return;
		await ActivateStoreAsync();
	}

	public event EventHandler<UnhandledExceptionEventArgs>? UnhandledException;

	/// <see cref="IActionSubscriber.SubscribeToAction{TAction}(object, Action{TAction})"/>
	public void SubscribeToAction<TAction>(object subscriber, Action<TAction> callback)
	{
		ActionSubscriber.SubscribeToAction(subscriber, callback);
	}

	/// <see cref="IActionSubscriber.UnsubscribeFromAllActions(object)"/>
	public void UnsubscribeFromAllActions(object subscriber)
	{
		ActionSubscriber.UnsubscribeFromAllActions(subscriber);
	}

	/// <see cref="IActionSubscriber.GetActionUnsubscriberAsIDisposable(object)"/>
	public IDisposable GetActionUnsubscriberAsIDisposable(object subscriber) =>
		ActionSubscriber.GetActionUnsubscriberAsIDisposable(subscriber);

	void IDisposable.Dispose()
	{
		if (!Disposed)
		{
			Disposed = true;
			Dispatcher.ActionDispatched -= ActionDispatched!;
		}
	}


	private void ActionDispatched(object sender, ActionDispatchedEventArgs e)
	{
		// Do not allow task dispatching inside a middleware-change.
		// These change cycles are for things like "jump to state" in Redux Dev Tools
		// and should be short lived.
		// We avoid dispatching inside a middleware change because we don't want UI events (like component Init)
		// that trigger actions (such as fetching data from a server) to execute
		if (IsInsideMiddlewareChange)
			return;


		// This is a concurrent queue, so is safe even if dequeuing is already in progress
		QueuedActions.Enqueue(e.Action);

		// HasActivatedStore is set to true when the page finishes loading
		// At which point DequeueActions will be called
		// So if it hasn't been activated yet, just exit and wait for that to happen
		if (!HasActivatedStore)
			return;

		// If a dequeue is still going then it will deal with the event we just
		// queued, so we can exit at this point.
		// This prevents a re-entrant deadlock
		if (!IsDispatching)
		{
			lock (SyncRoot)
			{
				DequeueActions();
			};
		}
	}

	private void EndMiddlewareChange(IDisposable[] disposables)
	{
		lock (SyncRoot)
		{
			BeginMiddlewareChangeCount--;
			if (BeginMiddlewareChangeCount == 0)
				disposables.ToList().ForEach(x => x.Dispose());
		}
	}

	private void TriggerEffects(object action)
	{
		var recordedExceptions = new List<Exception>();
		var effectsToExecute = Effects
			.Where(x => x.ShouldReactToAction(action))
			.ToArray();
		var executedEffects = new List<Task>();

		Action<Exception> collectExceptions = e =>
		{
			if (e is AggregateException aggregateException)
				recordedExceptions.AddRange(aggregateException.Flatten().InnerExceptions);
			else
				recordedExceptions.Add(e);
		};

		// Execute all tasks. Some will execute synchronously and complete immediately,
		// so we need to catch their exceptions in the loop so they don't prevent
		// other effects from executing.
		// It's then up to the UI to decide if any of those exceptions should cause
		// the app to terminate or not.
		foreach (IEffect effect in effectsToExecute)
		{
			try
			{
				executedEffects.Add(effect.HandleAsync(action, Dispatcher));
			}
			catch (Exception e)
			{
				collectExceptions(e);
			}
		}

		Task.Run(async () =>
		{
			try
			{
				await Task.WhenAll(executedEffects);
			}
			catch (Exception e)
			{
				collectExceptions(e);
			}

			// Let the UI decide if it wishes to deal with any unhandled exceptions.
			// By default it should throw the exception if it is not handled.
			foreach (Exception exception in recordedExceptions)
				UnhandledException?.Invoke(this, new Exceptions.UnhandledExceptionEventArgs(exception));
		});
	}

	private async Task InitializeMiddlewaresAsync()
	{
		foreach (IMiddleware middleware in Middlewares)
		{
			await middleware.InitializeAsync(Dispatcher, this);
		}
		Middlewares.ForEach(x => x.AfterInitializeAllMiddlewares());
	}

	private void ExecuteMiddlewareBeforeDispatch(object actionAboutToBeDispatched)
	{
		foreach (IMiddleware middleWare in Middlewares)
			middleWare.BeforeDispatch(actionAboutToBeDispatched);
	}

	private void ExecuteMiddlewareAfterDispatch(object actionJustDispatched)
	{
		Middlewares.ForEach(x => x.AfterDispatch(actionJustDispatched));
	}

	private async Task ActivateStoreAsync()
	{
		if (HasActivatedStore)
			return;

		await InitializeMiddlewaresAsync();

		lock (SyncRoot)
		{
#if NET6_0_OR_GREATER
				if (PersistenceManager is not null)
				{
					//Rehydrate as necessary
					Dispatcher.Dispatch(new StoreRehydratingAction());
				}
#endif
				HasActivatedStore = true;
				DequeueActions();

			InitializedCompletionSource.SetResult(true);
		}
	}

	private void DequeueActions()
	{
		if (IsDispatching)
			return;

		IsDispatching = true;
		try
		{
				//Only persist the store state if the action(s) in the queue don't consist solely of any combination of the following
				if (!QueuedActions.IsEmpty && !QueuedActions.All(action => action is StoreInitializedAction or StoreRehydratingAction or StoreRehydratedAction or StorePersistingAction or StorePersistedAction))
				{
					//Add an action to the end of the queue that persists the results of the actions to state, skipping the dispatcher approach (which might lead to an infinite loop)
					QueuedActions.Enqueue(new StorePersistingAction());
				}

			while (QueuedActions.TryDequeue(out object? nextActionToProcess))
			{
				// Only process the action if no middleware vetos it
				if (Middlewares.All(x => x.MayDispatchAction(nextActionToProcess)))
				{
					ExecuteMiddlewareBeforeDispatch(nextActionToProcess);

					// Notify all features of this action
					foreach (var featureInstance in FeaturesByName.Values)
						featureInstance.ReceiveDispatchNotificationFromStore(nextActionToProcess);

					ActionSubscriber?.Notify(nextActionToProcess);
					ExecuteMiddlewareAfterDispatch(nextActionToProcess);
					TriggerEffects(nextActionToProcess);
				}
			}
		}
		finally
		{
			IsDispatching = false;
		}
	}

#if NET6_0_OR_GREATER

		public string SerializeToJson()
		{
			var rootObj = new JsonObject();
			foreach (var kv in FeaturesByName)
			{
				var featureName = kv.Value.GetName();
				var featureValue = kv.Value.GetState();
				rootObj[featureName] = JsonSerializer.SerializeToNode(featureValue);
			}

			return JsonSerializer.Serialize(rootObj);
		}

		public void RehydrateFromJson(string json)
		{
			var obj = JsonDocument.Parse(json);

			foreach (var feature in obj.RootElement.EnumerateObject())
			{
				//Replace the state in the named feature with what's in the serialized data
				if (Features.ContainsKey(feature.Name))
				{
					var stateType = Features[feature.Name].GetStateType();
					var featureValue = feature.Value.Deserialize(stateType);

					if (featureValue is null)
						continue;

					FeaturesByName[feature.Name].RestoreState(featureValue);
				}
			}
		}

#endif
}
