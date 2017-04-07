﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Debugger.Impl;

namespace dnSpy.Debugger.Breakpoints.Code {
	abstract class DbgCodeBreakpointsService2 : DbgCodeBreakpointsService {
		public abstract void UpdateIsDebugging_DbgThread(bool newIsDebugging);
		public abstract DbgBoundCodeBreakpoint[] AddBoundBreakpoints_DbgThread(IList<DbgBoundCodeBreakpoint> boundBreakpoints);
		public abstract void RemoveBoundBreakpoints_DbgThread(IList<DbgBoundCodeBreakpoint> boundBreakpoints);
		public abstract DbgBoundCodeBreakpoint[] RemoveBoundBreakpoints_DbgThread(DbgRuntime runtime);
	}

	[Export(typeof(DbgCodeBreakpointsService))]
	[Export(typeof(DbgCodeBreakpointsService2))]
	sealed class DbgCodeBreakpointsServiceImpl : DbgCodeBreakpointsService2 {
		readonly object lockObj;
		readonly HashSet<DbgCodeBreakpointImpl> breakpoints;
		readonly Dictionary<DbgBreakpointLocation, DbgCodeBreakpointImpl> locationToBreakpoint;
		readonly DbgDispatcher dbgDispatcher;
		int breakpointId;
		bool isDebugging;

		public override event EventHandler<BoundBreakpointsMessageChangedEventArgs> BoundBreakpointsMessageChanged;

		internal DbgDispatcher DbgDispatcher => dbgDispatcher;

		[ImportingConstructor]
		DbgCodeBreakpointsServiceImpl(DbgDispatcher dbgDispatcher, [ImportMany] IEnumerable<Lazy<IDbgCodeBreakpointsServiceListener>> dbgCodeBreakpointsServiceListener) {
			lockObj = new object();
			breakpoints = new HashSet<DbgCodeBreakpointImpl>();
			locationToBreakpoint = new Dictionary<DbgBreakpointLocation, DbgCodeBreakpointImpl>();
			this.dbgDispatcher = dbgDispatcher;
			breakpointId = 0;
			isDebugging = false;

			foreach (var lz in dbgCodeBreakpointsServiceListener)
				lz.Value.Initialize(this);
		}

		void Dbg(Action action) => dbgDispatcher.Dbg(action);

		public override void Modify(DbgCodeBreakpointAndSettings[] settings) {
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));
			Dbg(() => ModifyCore(settings));
		}

		void ModifyCore(DbgCodeBreakpointAndSettings[] settings) {
			dbgDispatcher.VerifyAccess();
			List<DbgCodeBreakpointImpl> updatedBreakpoints = null;
			var bps = new List<DbgCodeBreakpointAndOldSettings>(settings.Length);
			lock (lockObj) {
				foreach (var info in settings) {
					var bpImpl = info.Breakpoint as DbgCodeBreakpointImpl;
					Debug.Assert(bpImpl != null);
					if (bpImpl == null)
						continue;
					Debug.Assert(breakpoints.Contains(bpImpl));
					if (!breakpoints.Contains(bpImpl))
						continue;
					var currentSettings = bpImpl.Settings;
					if (currentSettings == info.Settings)
						continue;
					bps.Add(new DbgCodeBreakpointAndOldSettings(bpImpl, currentSettings));
					if (bpImpl.WriteSettings_DbgThread(info.Settings)) {
						if (updatedBreakpoints == null)
							updatedBreakpoints = new List<DbgCodeBreakpointImpl>(settings.Length);
						updatedBreakpoints.Add(bpImpl);
					}
				}
			}
			if (bps.Count > 0)
				BreakpointsModified?.Invoke(this, new DbgBreakpointsModifiedEventArgs(new ReadOnlyCollection<DbgCodeBreakpointAndOldSettings>(bps)));
			if (updatedBreakpoints != null) {
				foreach (var bp in updatedBreakpoints)
					bp.RaiseBoundBreakpointsMessageChanged_DbgThread();
				BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(updatedBreakpoints.ToArray())));
			}
		}

		public override event EventHandler<DbgBreakpointsModifiedEventArgs> BreakpointsModified;

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgCodeBreakpoint>> BreakpointsChanged;
		public override DbgCodeBreakpoint[] Breakpoints {
			get {
				lock (lockObj)
					return breakpoints.ToArray();
			}
		}

		public override DbgCodeBreakpoint[] Add(DbgCodeBreakpointInfo[] breakpoints) {
			if (breakpoints == null)
				throw new ArgumentNullException(nameof(breakpoints));
			var bpImpls = new List<DbgCodeBreakpointImpl>(breakpoints.Length);
			List<DbgObject> objsToClose = null;
			lock (lockObj) {
				for (int i = 0; i < breakpoints.Length; i++) {
					var location = breakpoints[i].Location;
					if (locationToBreakpoint.ContainsKey(location)) {
						if (objsToClose == null)
							objsToClose = new List<DbgObject>();
						objsToClose.Add(location);
					}
					else {
						var bp = new DbgCodeBreakpointImpl(this, breakpointId++, location, breakpoints[i].Settings, isDebugging);
						bpImpls.Add(bp);
					}
				}
				Dbg(() => AddCore(bpImpls, objsToClose));
			}
			return bpImpls.ToArray();
		}

		void AddCore(List<DbgCodeBreakpointImpl> breakpoints, List<DbgObject> objsToClose) {
			dbgDispatcher.VerifyAccess();
			var added = new List<DbgCodeBreakpoint>(breakpoints.Count);
			List<DbgCodeBreakpointImpl> updatedBreakpoints = null;
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					Debug.Assert(!this.breakpoints.Contains(bp));
					if (this.breakpoints.Contains(bp))
						continue;
					if (locationToBreakpoint.ContainsKey(bp.Location)) {
						if (objsToClose == null)
							objsToClose = new List<DbgObject>();
						objsToClose.Add(bp);
					}
					else {
						added.Add(bp);
						this.breakpoints.Add(bp);
						locationToBreakpoint.Add(bp.Location, bp);
						if (bp.WriteIsDebugging_DbgThread(isDebugging)) {
							if (updatedBreakpoints == null)
								updatedBreakpoints = new List<DbgCodeBreakpointImpl>(breakpoints.Count);
							updatedBreakpoints.Add(bp);
						}
					}
				}
			}
			if (objsToClose != null) {
				foreach (var obj in objsToClose)
					obj.Close(dbgDispatcher.DispatcherThread);
			}
			if (added.Count > 0)
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgCodeBreakpoint>(added, added: true));
			if (updatedBreakpoints != null) {
				foreach (var bp in updatedBreakpoints)
					bp.RaiseBoundBreakpointsMessageChanged_DbgThread();
				BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(updatedBreakpoints.ToArray())));
			}
		}

		public override void Remove(DbgCodeBreakpoint[] breakpoints) {
			if (breakpoints == null)
				throw new ArgumentNullException(nameof(breakpoints));
			Dbg(() => RemoveCore(breakpoints));
		}

		void RemoveCore(DbgCodeBreakpoint[] breakpoints) {
			dbgDispatcher.VerifyAccess();
			var removed = new List<DbgCodeBreakpoint>(breakpoints.Length);
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					var bpImpl = bp as DbgCodeBreakpointImpl;
					Debug.Assert(bpImpl != null);
					if (bpImpl == null)
						continue;
					Debug.Assert(this.breakpoints.Contains(bpImpl));
					if (!this.breakpoints.Contains(bpImpl))
						continue;
					removed.Add(bpImpl);
					this.breakpoints.Remove(bpImpl);
					bool b = locationToBreakpoint.Remove(bpImpl.Location);
					Debug.Assert(b);
				}
			}
			if (removed.Count > 0) {
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgCodeBreakpoint>(removed, added: false));
				foreach (var bp in removed)
					bp.Close(dbgDispatcher.DispatcherThread);
			}
		}

		public override void Clear() => Dbg(() => ClearCore());
		void ClearCore() {
			dbgDispatcher.VerifyAccess();
			DbgCodeBreakpoint[] removed;
			lock (lockObj) {
				removed = breakpoints.ToArray();
				breakpoints.Clear();
				locationToBreakpoint.Clear();
			}
			if (removed.Length > 0) {
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgCodeBreakpoint>(removed, added: false));
				foreach (var bp in removed)
					bp.Close(dbgDispatcher.DispatcherThread);
			}
		}

		public override void UpdateIsDebugging_DbgThread(bool newIsDebugging) {
			dbgDispatcher.VerifyAccess();
			List<DbgCodeBreakpointImpl> updatedBreakpoints;
			lock (lockObj) {
				if (isDebugging == newIsDebugging)
					return;
				isDebugging = newIsDebugging;
				updatedBreakpoints = new List<DbgCodeBreakpointImpl>(breakpoints.Count);
				foreach (var bp in breakpoints) {
					bool updated = bp.WriteIsDebugging_DbgThread(isDebugging);
					if (updated)
						updatedBreakpoints.Add(bp);
				}
			}
			foreach (var bp in updatedBreakpoints)
				bp.RaiseBoundBreakpointsMessageChanged_DbgThread();
			if (updatedBreakpoints.Count > 0)
				BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(updatedBreakpoints.ToArray())));
		}

		public override DbgBoundCodeBreakpoint[] AddBoundBreakpoints_DbgThread(IList<DbgBoundCodeBreakpoint> boundBreakpoints) {
			dbgDispatcher.VerifyAccess();
			var dict = CreateBreakpointDictionary(boundBreakpoints);
			var updatedBreakpoints = new List<(bool raiseMessageChanged, DbgCodeBreakpointImpl breakpoint, List<DbgBoundCodeBreakpoint> boundBreakpoints)>(dict.Count);
			List<DbgBoundCodeBreakpoint> unusedBoundBreakpoints = null;
			lock (lockObj) {
				foreach (var kv in dict) {
					var bp = kv.Key;
					if (breakpoints.Contains(bp)) {
						bool raiseMessageChanged = bp.AddBoundBreakpoints_DbgThread(kv.Value);
						updatedBreakpoints.Add((raiseMessageChanged, bp, kv.Value));
					}
					else {
						if (unusedBoundBreakpoints == null)
							unusedBoundBreakpoints = new List<DbgBoundCodeBreakpoint>();
						unusedBoundBreakpoints.AddRange(kv.Value);
					}
				}
			}
			foreach (var info in updatedBreakpoints)
				info.breakpoint.RaiseEvents_DbgThread(info.raiseMessageChanged, info.boundBreakpoints, added: true);
			if (updatedBreakpoints.Count > 0)
				BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(updatedBreakpoints.Where(a => a.raiseMessageChanged).Select(a => a.breakpoint).ToArray())));
			return unusedBoundBreakpoints?.ToArray() ?? Array.Empty<DbgBoundCodeBreakpoint>();
		}

		public override void RemoveBoundBreakpoints_DbgThread(IList<DbgBoundCodeBreakpoint> boundBreakpoints) {
			dbgDispatcher.VerifyAccess();
			var dict = CreateBreakpointDictionary(boundBreakpoints);
			var updatedBreakpoints = new List<(bool raiseMessageChanged, DbgCodeBreakpointImpl breakpoint, List<DbgBoundCodeBreakpoint> boundBreakpoints)>(dict.Count);
			lock (lockObj) {
				foreach (var kv in dict) {
					var bp = kv.Key;
					if (breakpoints.Contains(bp)) {
						bool raiseMessageChanged = bp.RemoveBoundBreakpoints_DbgThread(kv.Value);
						updatedBreakpoints.Add((raiseMessageChanged, bp, kv.Value));
					}
				}
			}
			foreach (var info in updatedBreakpoints)
				info.breakpoint.RaiseEvents_DbgThread(info.raiseMessageChanged, info.boundBreakpoints, added: false);
			if (updatedBreakpoints.Count > 0)
				BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(updatedBreakpoints.Where(a => a.raiseMessageChanged).Select(a => a.breakpoint).ToArray())));
		}

		static Dictionary<DbgCodeBreakpointImpl, List<DbgBoundCodeBreakpoint>> CreateBreakpointDictionary(IList<DbgBoundCodeBreakpoint> boundBreakpoints) {
			int count = boundBreakpoints.Count;
			var dict = new Dictionary<DbgCodeBreakpointImpl, List<DbgBoundCodeBreakpoint>>(count);
			for (int i = 0; i < boundBreakpoints.Count; i++) {
				var bound = boundBreakpoints[i];
				var bpImpl = bound.Breakpoint as DbgCodeBreakpointImpl;
				Debug.Assert(bpImpl != null);
				if (bpImpl == null)
					continue;
				if (!dict.TryGetValue(bpImpl, out var list))
					dict.Add(bpImpl, list = new List<DbgBoundCodeBreakpoint>());
				list.Add(bound);
			}
			return dict;
		}

		public override DbgBoundCodeBreakpoint[] RemoveBoundBreakpoints_DbgThread(DbgRuntime runtime) {
			dbgDispatcher.VerifyAccess();
			var list = new List<DbgBoundCodeBreakpoint>();
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					foreach (var boundBreakpoint in bp.BoundBreakpoints) {
						if (boundBreakpoint.Runtime == runtime)
							list.Add(boundBreakpoint);
					}
				}
			}
			var res = list.ToArray();
			RemoveBoundBreakpoints_DbgThread(res);
			return res;
		}

		internal void OnBoundBreakpointsMessageChanged_DbgThread(DbgCodeBreakpointImpl bp) {
			dbgDispatcher.VerifyAccess();
			bp.RaiseBoundBreakpointsMessageChanged_DbgThread();
			BoundBreakpointsMessageChanged?.Invoke(this, new BoundBreakpointsMessageChangedEventArgs(new ReadOnlyCollection<DbgCodeBreakpoint>(new[] { bp })));
		}
	}
}