// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using System;
using Polytoria.Attributes;
using Polytoria.Scripting;
using Polytoria.Scripting.Luau;
using System.Collections.Generic;

namespace Polytoria.Datamodel.Services;

[Static("Hooks"), ExplorerExclude, SaveIgnore]
public sealed partial class HookService : Instance
{
	private readonly List<ValueType> _queue = [];

	[ScriptProperty]
	public PTSignal<double> Updated { get; private set; } = new();
	[ScriptProperty]
	public PTSignal<double> PreRendered { get; private set; } = new();
	[ScriptProperty]
	public PTSignal<double> PostRendered { get; private set; } = new();
	[ScriptProperty]
	public PTSignal<double> PhysicsUpdated { get; private set; } = new();

	public override void Init()
	{
		base.Init();
		SetProcess(true);
		SetPhysicsProcess(true);
	}

	public override void Ready()
	{
		base.Ready();
		// NOTE: Godot doesn't pass deltatime to the frame_pre_draw or
		// frame_post_draw signals, so we have to grab it manually using
		// Node.GetProcessDeltaTime()
		RenderingServer.Singleton.Connect(
			RenderingServer.SignalName.FramePreDraw,
			Callable.From(OnFramePreDraw)
		);
		RenderingServer.Singleton.Connect(
			RenderingServer.SignalName.FramePostDraw,
			Callable.From(OnFramePostDraw)
		);
	}

	public override void Process(double delta)
	{
		Updated.Invoke(delta);
		DrainQueue();
		base.Process(delta);
	}

	public override void PhysicsProcess(double delta)
	{
		PhysicsUpdated.Invoke(delta);
		base.PhysicsProcess(delta);
	}

	private void OnFramePreDraw()
	{
		PreRendered.Invoke(GDNode.GetProcessDeltaTime());
	}

	private void OnFramePostDraw()
	{
		PostRendered.Invoke(GDNode.GetProcessDeltaTime());
	}

	/// <summary>
	/// Queues a thread's first resumption for the next drain. Used by 'spawn'
	/// so a burst of spawns in one script doesn't cascade into the caller's
	/// own call stack.
	/// </summary>
	internal void EnqueueSpawn(LuaState thread, int threadRef, int numArgs)
	{
		_queue.Add(new QueuedResume(thread, threadRef, numArgs));
	}

	/// <summary>
	/// Queues resolve to run once Root.UpTime reaches wakeTime.
	/// </summary>
	internal void EnqueueTimed(decimal wakeTime, Action resolve)
	{
		_queue.Add(new TimedEntry(wakeTime, resolve));
	}

	/// <summary>
	/// Queues a PTCallback call for the next drain.
	/// </summary>
	internal void EnqueueCallback(PTCallback callback, object?[] args)
	{
		_queue.Add(new DeferredCallback(callback, args));
	}

	/// <summary>
	/// Dequeues all of a PTCallback's calls from the next drain.
	/// </summary>
	internal void DequeueCallback(PTCallback callback)
	{
		_queue.RemoveAll(v => v is DeferredCallback dc && dc.Callback == callback);
	}

	private void DrainQueue()
	{
		if (_queue.Count == 0) return;

		decimal now = Root.UpTime;
		_queue.RemoveAll(v => {
			switch (v)
			{
				case QueuedResume queuedResume:
					async void run()
					{
						try
						{
							await LuauProvider.ResumeThread(queuedResume.Thread, null, queuedResume.NumArgs);
						}
						finally
						{
							queuedResume.Thread.Unref(queuedResume.ThreadRef);
						}
					}
					run();
					break;
				case TimedEntry timedEntry:
					if (timedEntry.WakeTime > now) return false;

					timedEntry.Resolve();
					break;
				case DeferredCallback deferredCallback:
					if (deferredCallback.Callback.Disposed) break;

					try
					{
						deferredCallback.Callback.InvokeDirect(deferredCallback.Args);
					}
					catch (Exception ex)
					{
						GD.PushError($"Deferred PTCallback Length: {deferredCallback.Args.Length} : " + ex.ToString());
					}
					break;
			}
			return true;
		});
	}

	internal readonly record struct QueuedResume(LuaState Thread, int ThreadRef, int NumArgs);

	internal readonly record struct TimedEntry(decimal WakeTime, Action Resolve);

	internal readonly record struct DeferredCallback(PTCallback Callback, object?[] Args);
}
