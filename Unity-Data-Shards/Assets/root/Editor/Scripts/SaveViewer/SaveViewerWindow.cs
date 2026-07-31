using System;
using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Attributes;
using Saesentsessis.Persistence.Configuration.Layout;
using Saesentsessis.Persistence.Configuration.Storage;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Threading;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Persistence.Editor.SaveViewer
{
	/// <summary>
	/// Lists the save slots a configured storage holds, and shows an envelope header for the one
	/// that is selected.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A thin shell over <see cref="SaveSlotBrowser"/> — everything shown here is available at
	/// runtime too, which is deliberate: a load-game screen needs the same data, and keeping the
	/// logic out of the editor assembly is what lets it be tested headlessly.
	/// </para>
	/// <para>
	/// <b>Configuration is serialized on the window itself</b>, so several windows can each point at
	/// a different backend, mirroring a project running several <see cref="SaveManager"/>s. Unity
	/// writes an <see cref="EditorWindow"/>'s serialized fields into the layout file, so the
	/// configuration survives a domain reload and an editor restart alike. It does <i>not</i>
	/// survive resetting the window layout — configuration is cheap to redo, so that is an accepted
	/// trade rather than a bug.
	/// </para>
	/// <para>
	/// <b>This window's own drawing allocates nothing per repaint.</b> <c>OnGUI</c> runs several
	/// times per frame for as long as the window is open, so every rect is computed rather than
	/// obtained from <c>EditorGUILayout</c> — whose scopes are classes, and whose
	/// <c>GUILayout.Width</c>-style options cost an object <i>and</i> a params array apiece — and
	/// every string shown is built once when the data behind it changes rather than once per
	/// repaint. What remains is inside Unity: <see cref="EditorGUI.PropertyField(Rect,SerializedProperty,bool)"/>
	/// and the drawers it dispatches to allocate on their own account, and iterating a
	/// <see cref="SerializedProperty"/>'s children inherently copies it.
	/// </para>
	/// </remarks>
	public sealed class SaveViewerWindow : EditorWindow
	{
		private const string MenuPath = "Tools/Saesentsessis/Persistence/Save Viewer";

		private const float Padding = 6f;
		private const float SlotListHeight = 160f;
		private const float ScrollbarWidth = 14f;
		private const float SizeColumnWidth = 80f;
		private const float KeysColumnWidth = 64f;
		private const float ModifiedColumnWidth = 130f;
		private const float ButtonWidth = 90f;
		private const float HelpBoxHeight = 38f;

		private static readonly Color SelectionTint = new(0.24f, 0.48f, 0.90f, 0.35f);
		private static readonly Color BackgroundTint = new(0f, 0f, 0f, 0.25f);

		#region Serialized configuration

		// Survives domain reloads and editor restarts through the layout file. Only the
		// configuration lives here — see the NonSerialized block for why the rest must not.
		[SerializeReference, SubclassPicker] private IStorageDescriptor storage;
		[SerializeReference, SubclassPicker] private ISaveLayoutDescriptor layout;

		[SerializeField] private Vector2 slotScroll;
		[SerializeField] private Vector2 detailScroll;
		[SerializeField] private string selectedSlot;
		[SerializeField] private bool configurationExpanded = true;

		#endregion

		#region Transient state

		// NOT serialized, and that is load-bearing. A domain reload kills any in-flight
		// continuation, so a serialized Phase.Loading would come back with nothing left alive to
		// finish it and the window would show "Loading…" forever. Starting over from Idle is both
		// correct and what the user expects after a recompile.
		[NonSerialized] private Phase _phase;
		[NonSerialized] private string _error;
		[NonSerialized] private List<SaveSlotInfo> _slots;
		[NonSerialized] private SaveSlotHeader _selectedHeader;
		[NonSerialized] private bool _selectedHeaderLoaded;
		[NonSerialized] private CancellationTokenSource _cancellation;
		[NonSerialized] private SerializedObject _serialized;

		// Resolved once. FindProperty builds a new SerializedProperty on every call, which is a
		// per-repaint allocation for something that never changes.
		[NonSerialized] private SerializedProperty _storageProperty;
		[NonSerialized] private SerializedProperty _layoutProperty;

		// Display strings, built when the data changes rather than when it is drawn.
		[NonSerialized] private SlotRow[] _rows;
		[NonSerialized] private int _rowCount;
		[NonSerialized] private string _slotsCaption;
		[NonSerialized] private string _detailCaption;
		[NonSerialized] private string[] _headerValues;

		// The live chain, rebuilt on each Refresh and held until the window closes or reloads.
		// Held rather than rebuilt per operation because building it can be expensive — an
		// AesCbcHmacTransformDescriptor re-reads its key file and re-derives subkeys every Create.
		[NonSerialized] private ISaveLayout _activeLayout;
		[NonSerialized] private SaveSlotBrowser _browser;

		// Operations in flight against the chain above. Cancellation is a request, not an event:
		// FileStorage polls an AsyncReadManager handle that is writing into a native buffer, and
		// that read completes when the OS says so. Disposing the chain first would free storage out
		// from under a live handle, so chains are retired here and disposed once nothing is running.
		[NonSerialized] private int _busy;
		[NonSerialized] private List<ISaveLayout> _retired;

		// Clicks are recorded here and acted on during the next Layout event. Starting an operation
		// mid-frame would change how many controls the rest of OnGUI emits, and IMGUI requires the
		// Layout and Repaint passes to agree on that count — disagreeing throws out of the window.
		[NonSerialized] private bool _refreshRequested;
		[NonSerialized] private bool _clearRequested;
		[NonSerialized] private string _selectionRequested;

		private enum Phase
		{
			Idle,
			Loading,
			Ready,
			Failed
		}

		/// <summary>One list row with its columns already formatted.</summary>
		private readonly struct SlotRow
		{
			public readonly string Slot;
			public readonly string Size;
			public readonly string Keys;
			public readonly string Modified;

			public SlotRow(string slot, string size, string keys, string modified)
			{
				Slot = slot;
				Size = size;
				Keys = keys;
				Modified = modified;
			}
		}

		#endregion

		[MenuItem(MenuPath)]
		private static void Open()
		{
			var window = GetWindow<SaveViewerWindow>();
			window.titleContent = new GUIContent("Save Viewer");
			window.minSize = new Vector2(460f, 512f);
			window.Show();
		}

		private void OnEnable()
		{
			// EditorWindow is a ScriptableObject, so it can front its own SerializedObject — which
			// is what lets the SubclassPicker drawer render the descriptor fields below.
			_serialized = new SerializedObject(this);
			_storageProperty = _serialized.FindProperty(nameof(storage));
			_layoutProperty = _serialized.FindProperty(nameof(layout));

			_slots = new List<SaveSlotInfo>();
			_retired = new List<ISaveLayout>();
			_rows = Array.Empty<SlotRow>();
			_headerValues = new string[HeaderFieldCount];
		}

		private void OnDisable()
		{
			// Called on close AND on domain reload, which is exactly when an in-flight operation
			// must stop: its continuation would otherwise resume against a dead window. It is also
			// the only chance to release the chain — a FileStorage that survives a reload leaks its
			// gate and path cache with nothing left holding a reference to it.
			CancelInFlight();
			ReleaseChain();
		}

		/// <summary>
		/// Stops any running operation. Always paired with <see cref="ReleaseChain"/> — disposing the
		/// storage out from under a read in flight would surface as an ObjectDisposedException from
		/// a continuation nobody is waiting on.
		/// </summary>
		private void CancelInFlight()
		{
			_cancellation?.Cancel();
			_cancellation?.Dispose();
			_cancellation = null;
		}

		/// <summary>
		/// Releases the built chain — the layout owns the storage, so one Dispose covers both.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Detaches the chain immediately but disposes it only once nothing is running against it.
		/// Cancelling does not stop an in-flight read: <c>FileStorage</c> polls an
		/// <c>AsyncReadManager</c> handle that is writing into a native buffer, and it completes
		/// when the OS says so. Disposing underneath that is a use-after-free on a background
		/// thread — precisely the failure this package refuses to ship anywhere else.
		/// </para>
		/// <para>
		/// A domain reload while a read is in flight is the one case nothing can rescue: the
		/// continuation that would have drained the retirement list dies with the domain, and Unity
		/// reports the native buffer as leaked. Recompiling mid-read is rare and self-announcing.
		/// </para>
		/// </remarks>
		private void ReleaseChain()
		{
			if (_activeLayout != null)
			{
				// Retired rather than disposed while busy — an operation may still be reading
				// through this very storage, and it holds its own reference to it.
				if (_busy > 0)
					_retired.Add(_activeLayout);
				else
					_activeLayout.Dispose();
			}

			_activeLayout = null;
			_browser = null;
		}

		/// <summary>Marks an operation finished and drains anything retired while it ran.</summary>
		private void EndOperation()
		{
			_busy--;

			if (_busy > 0 || _retired.Count == 0)
				return;

			for (var i = 0; i < _retired.Count; i++)
				_retired[i].Dispose();

			_retired.Clear();
		}

		private void OnGUI()
		{
			// OnGUI stays synchronous. It runs once per event, many times per frame, so awaiting
			// here would mean nothing — work is started below and the window repaints when it lands.
			//
			// Requests are drained on Layout, the first event of a frame, so any state an operation
			// changes synchronously is already settled before Repaint counts controls.
			if (Event.current.type == EventType.Layout)
				DrainRequests();

			_serialized.Update();

			// A single cursor threaded through the draw calls, each returning where it finished.
			// This is what replaces EditorGUILayout: no scopes, no options, no allocation.
			var width = position.width - Padding * 2f;
			var y = Padding;

			y = DrawConfiguration(y, width);
			y = DrawStatusBar(y, width);
			y = DrawSlotList(y, width);

			DrawSelectedHeader(y, width);

			_serialized.ApplyModifiedProperties();
		}

		private void DrainRequests()
		{
			if (_clearRequested)
			{
				_clearRequested = false;
				ClearResults();
			}

			if (_refreshRequested)
			{
				_refreshRequested = false;
				Refresh();
			}

			if (_selectionRequested == null)
				return;

			var slot = _selectionRequested;
			_selectionRequested = null;
			Select(slot);
		}

		#region Drawing

		private static float LineHeight => EditorGUIUtility.singleLineHeight;
		private static float Spacing => EditorGUIUtility.standardVerticalSpacing;

		/// <remarks>
		/// Uses a plain <see cref="EditorGUI.Foldout(Rect,bool,string,bool,GUIStyle)"/> rather than
		/// <c>BeginFoldoutHeaderGroup</c>, and that is a correctness requirement, not a style choice.
		/// A header group cannot be nested, and this section's content is arbitrary property UI: a
		/// descriptor tree that draws a foldout header of its own would nest one inside this.
		/// <para>
		/// The failure is worse than a wrong-looking section. Unity guards nesting with a static
		/// counter that is explicitly excluded from per-frame static cleanup, and the nested call
		/// returns <c>false</c> — so a caller written as <c>if (Begin(…)) { … End(); }</c> skips its
		/// <c>End</c>, leaving the counter stuck above zero for the rest of the domain's life. From
		/// then on <i>every</i> foldout header in the editor draws "You can't nest Foldout Headers"
		/// instead of its content, including the Inspector's own component headers, and nothing
		/// short of a domain reload clears it.
		/// </para>
		/// </remarks>
		private float DrawConfiguration(float y, float width)
		{
			var headerRect = new Rect(Padding, y, width, LineHeight + Spacing * 2f);

			configurationExpanded = EditorGUI.Foldout(headerRect, configurationExpanded, "Configuration",
				toggleOnLabelClick: true, EditorStyles.foldoutHeader);

			y += headerRect.height;

			if (configurationExpanded)
			{
				y = DrawProperty(_storageProperty, y, width);
				y = DrawProperty(_layoutProperty, y, width);
				y += Spacing;

				var buttonY = y;

				using (new EditorGUI.DisabledScope(_phase == Phase.Loading))
				{
					var refreshRect = new Rect(Padding, buttonY, ButtonWidth, LineHeight);

					if (GUI.Button(refreshRect, "Refresh"))
					{
						// Applied here, not in the deferred handler: a value typed into a field this
						// frame is not on the object until the SerializedObject is flushed, and the
						// descriptors are read when the request drains.
						_serialized.ApplyModifiedProperties();
						_refreshRequested = true;
					}

					using (new EditorGUI.DisabledScope(_rowCount == 0))
					{
						var clearRect = new Rect(Padding + ButtonWidth + Spacing, buttonY, ButtonWidth, LineHeight);

						if (GUI.Button(clearRect, "Clear"))
							_clearRequested = true;
					}
				}

				y += LineHeight + Spacing;
			}

			return y + Spacing;
		}

		private static float DrawProperty(SerializedProperty property, float y, float width)
		{
			var height = EditorGUI.GetPropertyHeight(property, true);

			EditorGUI.PropertyField(new Rect(Padding, y, width, height), property, true);

			return y + height + Spacing;
		}

		private float DrawStatusBar(float y, float width)
		{
			var message = _phase switch
			{
				Phase.Idle => "Configure a storage and a layout, then press Refresh.",
				Phase.Loading => "Reading…",
				Phase.Failed => _error,
				Phase.Ready when _rowCount == 0 => "No saves found.",
				_ => null
			};

			if (message == null)
				return y;

			var type = _phase == Phase.Failed ? MessageType.Error : MessageType.Info;

			EditorGUI.HelpBox(new Rect(Padding, y, width, HelpBoxHeight), message, type);

			return y + HelpBoxHeight + Spacing;
		}

		private float DrawSlotList(float y, float width)
		{
			if (_phase != Phase.Ready || _rowCount == 0)
				return y;

			EditorGUI.LabelField(new Rect(Padding, y, width, LineHeight), _slotsCaption, EditorStyles.boldLabel);
			y += LineHeight + Spacing;

			var rowHeight = LineHeight + Spacing;
			var totalHeight = rowHeight * _rowCount;
			var listRect = new Rect(Padding, y, width, SlotListHeight);
			var contentWidth = width - (SlotListHeight < totalHeight ? ScrollbarWidth : 0);
			var viewRect = new Rect(0f, 0f, contentWidth, _rowCount * rowHeight);

			EditorGUI.DrawRect(listRect, BackgroundTint);
			
			// GUI.BeginScrollView rather than the EditorGUILayout scope: same behavior, and it
			// takes and returns a Vector2 instead of allocating a disposable wrapper per repaint.
			slotScroll = GUI.BeginScrollView(listRect, slotScroll, viewRect);

			for (var i = 0; i < _rowCount; i++)
			{
				ref readonly var row = ref _rows[i];
				var rowRect = new Rect(0f, i * rowHeight, contentWidth, LineHeight);

				if (row.Slot == selectedSlot)
					EditorGUI.DrawRect(rowRect, SelectionTint);

				var columnsX = rowRect.width - (SizeColumnWidth + KeysColumnWidth + ModifiedColumnWidth);

				if (GUI.Button(new Rect(0f, rowRect.y, columnsX, LineHeight), row.Slot, EditorStyles.label))
					_selectionRequested = row.Slot;

				var x = columnsX;

				EditorGUI.LabelField(new Rect(x, rowRect.y, SizeColumnWidth, LineHeight), row.Size);
				x += SizeColumnWidth;

				EditorGUI.LabelField(new Rect(x, rowRect.y, KeysColumnWidth, LineHeight), row.Keys);
				x += KeysColumnWidth;

				EditorGUI.LabelField(new Rect(x, rowRect.y, ModifiedColumnWidth, LineHeight), row.Modified);
			}

			GUI.EndScrollView();

			return y + SlotListHeight + Spacing;
		}

		private void DrawSelectedHeader(float y, float width)
		{
			if (_phase != Phase.Ready || string.IsNullOrEmpty(selectedSlot))
				return;

			EditorGUI.LabelField(new Rect(Padding, y, width, LineHeight), _detailCaption, EditorStyles.boldLabel);
			y += LineHeight + Spacing;

			if (_selectedHeaderLoaded == false)
			{
				EditorGUI.HelpBox(new Rect(Padding, y, width, HelpBoxHeight), "Reading header…", MessageType.Info);
				return;
			}

			// Every other field is meaningless unless the read succeeded, so the status decides
			// whether they are worth drawing at all.
			if (_selectedHeader.Status != SaveSlotStatus.Ok)
			{
				EditorGUI.HelpBox(new Rect(Padding, y, width, HelpBoxHeight),
					DescribeStatus(_selectedHeader.Status), MessageType.Warning);
				return;
			}

			var rowHeight = LineHeight + Spacing;
			var available = Mathf.Max(position.height - y - Padding, rowHeight);
			var listRect = new Rect(Padding, y, width, available);
			var contentWidth = width - ScrollbarWidth;
			var viewRect = new Rect(0f, 0f, contentWidth, HeaderFieldCount * rowHeight);

			detailScroll = GUI.BeginScrollView(listRect, detailScroll, viewRect);

			using (new EditorGUI.DisabledScope(true))
			{
				for (var i = 0; i < HeaderFieldCount; i++)
				{
					var rect = new Rect(0f, i * rowHeight, contentWidth, LineHeight);

					EditorGUI.LabelField(rect, HeaderFieldNames[i], _headerValues[i]);
				}
			}

			GUI.EndScrollView();
		}

		#endregion

		#region Operations

		/// <summary>
		/// Lists the slots. <c>async void</c> is correct here and only here — this is an event
		/// handler, not something anyone awaits — which is exactly why the body cannot be allowed
		/// to throw: an escaping exception would surface at an unrelated moment, or not at all.
		/// </summary>
		private async void Refresh()
		{
			if (_phase == Phase.Loading)
				return;

			if (storage == null || layout == null)
			{
				Fail("Choose both a storage and a layout before refreshing.");
				return;
			}

			CancelInFlight();
			_cancellation = new CancellationTokenSource();

			var token = _cancellation.Token;

			_phase = Phase.Loading;
			_error = null;
			_slots.Clear();
			_rowCount = 0;
			_selectedHeaderLoaded = false;
			Repaint();

			// Claimed before the first await, so a release requested while this runs is deferred.
			_busy++;

			try
			{
				// Replaces whatever the previous refresh built — the descriptors may have changed.
				// Safe here because nothing else is running: this operation has not touched the
				// chain yet, and a Select in flight would have kept _busy above zero, deferring it.
				ReleaseChain();

				// The storage is kept alongside the layout because the browser needs it directly,
				// while the layout is what owns and disposes it. Until the layout exists nothing
				// owns the storage, so a throw from Create would strand it — hence the hand-off.
				var builtStorage = storage.Create();

				try
				{
					_activeLayout = layout.Create(builtStorage);
				}
				catch
				{
					builtStorage.Dispose();
					throw;
				}

				if (_activeLayout is not ISlotKeyMapper mapper)
				{
					Fail($"{_activeLayout.GetType().Name} does not implement ISlotKeyMapper, so its " +
						"keys cannot be grouped into slots.");
					return;
				}

				_browser = new SaveSlotBrowser(builtStorage, mapper);

				if (_browser.CanList == false)
				{
					Fail($"{builtStorage.GetType().Name} cannot enumerate its keys, so its slots " +
						"cannot be listed. PlayerPrefs is the usual case — it exposes no key listing.");
					return;
				}

				await _browser.PopulateAsync(_slots, token);

				// Explicit hop rather than trusting the ambient SynchronizationContext: without
				// UniTask a Task continuation resumes wherever that context says, and touching
				// EditorWindow state off the main thread is undefined.
				await PersistenceTask.SwitchToMainThread(token);

				_slots.Sort(ByRecencyThenName);
				BuildRows();

				_phase = Phase.Ready;

				if (string.IsNullOrEmpty(selectedSlot))
					return;

				// A slot that no longer exists must not keep a stale header on screen. One that does
				// still exist has to be re-read rather than merely kept: selectedSlot is serialized
				// and survives a domain reload, while the header beside it does not, so leaving it
				// alone would strand the panel on "Reading header…" with nothing on the way.
				if (ContainsSlot(selectedSlot))
					_selectionRequested = selectedSlot;
				else
					selectedSlot = null;
			}
			catch (OperationCanceledException)
			{
				// The window closed, or a newer refresh superseded this one. Nothing to report.
			}
			catch (Exception exception)
			{
				Fail(exception.Message);
			}
			finally
			{
				EndOperation();
				Repaint();
			}
		}

		private async void Select(string slot)
		{
			selectedSlot = slot;
			_selectedHeaderLoaded = false;
			_detailCaption = "Envelope — " + slot;
			Repaint();

			// Only reachable after a successful Refresh, so the chain is already built.
			if (_browser == null)
				return;

			var token = _cancellation?.Token ?? CancellationToken.None;

			// Captured before the first await: a Refresh landing meanwhile replaces the field, and
			// this read must keep using the storage it started against — which the retirement list
			// keeps alive until this operation reports itself finished.
			var browser = _browser;

			_busy++;

			try
			{
				var header = await browser.ReadHeaderAsync(slot, token);

				await PersistenceTask.SwitchToMainThread(token);

				// Guard against a slower read landing after a newer click: only the most recent
				// selection may write to the panel.
				if (selectedSlot != slot)
					return;

				_selectedHeader = header;
				BuildHeaderValues();
				_selectedHeaderLoaded = true;
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception exception)
			{
				Fail(exception.Message);
			}
			finally
			{
				EndOperation();
				Repaint();
			}
		}

		private void ClearResults()
		{
			_slots.Clear();
			_rowCount = 0;
			selectedSlot = null;
			_selectedHeaderLoaded = false;
			_phase = Phase.Idle;
			_error = null;

			// Releases file handles and key material rather than holding them for a window the user
			// has visibly finished with. Cancel first: a header read may still be using the chain.
			CancelInFlight();
			ReleaseChain();
			Repaint();
		}

		private void Fail(string message)
		{
			_error = message;
			_phase = Phase.Failed;
			Repaint();
		}

		private bool ContainsSlot(string slot)
		{
			// A plain loop rather than List.Exists: the predicate would capture `this` and allocate
			// a closure on a path that runs for every refresh.
			for (var i = 0; i < _slots.Count; i++)
				if (_slots[i].Slot == slot)
					return true;

			return false;
		}

		#endregion

		#region Row and value construction

		private static readonly string[] HeaderFieldNames =
		{
			"Format version",
			"Written (UTC)",
			"Shards",
			"Distinct types",
			"Checksum"
		};

		private static readonly int HeaderFieldCount = HeaderFieldNames.Length;

		/// <summary>
		/// Formats every visible column once per refresh.
		/// </summary>
		/// <remarks>
		/// Doing it here rather than in the draw call is the difference between four strings per
		/// slot per repaint — several hundred a second on a list of any size — and four per slot
		/// per refresh. The backing array is reused and only grows.
		/// </remarks>
		private void BuildRows()
		{
			if (_rows.Length < _slots.Count)
				_rows = new SlotRow[Mathf.NextPowerOfTwo(Mathf.Max(_slots.Count, 8))];

			for (var i = 0; i < _slots.Count; i++)
			{
				var slot = _slots[i];

				_rows[i] = new SlotRow(
					slot.Slot,
					FormatBytes(slot.TotalBytes),
					slot.KeyCount == 1 ? "1 key" : slot.KeyCount + " keys",
					// A backend with no concept of a write time reports zero rather than lying.
					slot.HasModifiedTime ? slot.ModifiedUtc.ToLocalTime().ToString("g") : "—");
			}

			_rowCount = _slots.Count;
			_slotsCaption = _rowCount == 1 ? "1 slot" : _rowCount + " slots";
		}

		private void BuildHeaderValues()
		{
			_headerValues[0] = _selectedHeader.FormatVersion.ToString();

			try
			{
				_headerValues[1] = _selectedHeader.WrittenUtc.ToString("u");
			}
			catch (Exception)
			{
				_headerValues[1] = "corrupted";
			}

			_headerValues[2] = _selectedHeader.RecordCount.ToString();
			_headerValues[3] = _selectedHeader.TypeCount.ToString();
			_headerValues[4] = "0x" + _selectedHeader.Checksum.ToString("x16");
		}

		// Newest first, because "which save is current" is the question a viewer exists to answer;
		// name breaks ties so the order is stable when nothing reports a time.
		private static readonly Comparison<SaveSlotInfo> ByRecencyThenName = static (left, right) =>
		{
			var byTime = right.ModifiedUtcTicks.CompareTo(left.ModifiedUtcTicks);

			return byTime != 0 ? byTime : string.CompareOrdinal(left.Slot, right.Slot);
		};

		private static string FormatBytes(long bytes)
		{
			if (bytes < 1024)
				return bytes + " B";

			if (bytes < 1024 * 1024)
				return (bytes / 1024f).ToString("0.#") + " KB";

			return (bytes / (1024f * 1024f)).ToString("0.##") + " MB";
		}

		private static string DescribeStatus(SaveSlotStatus status)
		{
			return status switch
			{
				SaveSlotStatus.Missing => "No data under this key.",
				SaveSlotStatus.Foreign => "Not a Data Shards save — the format marker is absent.",
				SaveSlotStatus.UnsupportedVersion => "Written by a format version this build cannot read.",
				_ => "The envelope failed its checksum or could not be decoded."
			};
		}

		#endregion
	}
}
