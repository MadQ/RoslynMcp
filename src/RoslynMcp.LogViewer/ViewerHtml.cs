namespace RoslynMcp.LogViewer;

// Will move to embedded resource (see scratchpad).
static class ViewerHtml
{
	public static string Page => """
	<!-- Developer tool — not a production web app. Accessibility (WCAG) not targeted unless requested. -->
	<!DOCTYPE html>
	<html lang="en">
	<head>
	  <meta charset="utf-8">
	  <meta name="viewport" content="width=device-width, initial-scale=1">
	  <title>RoslynMcp Logs</title>
	  <style>
		*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
		
		body {
		  background: rgb(214 211 171);
		  color: rgb(0 0 0);
		  font-family: 'Cascadia Code', 'Consolas', 'Courier New', monospace;
		  font-size: 13px;
		  display: flex;
		  flex-direction: column;
		  height: 100vh;
		  overflow: hidden;
		}
		
		/* ── Toolbar ── */
		#toolbar {
		  padding: 7px 12px;
		  background: rgb(196 193 150);
		  border-bottom: 1px solid rgb(160 155 110);
		  display: flex;
		  gap: 6px;
		  align-items: center;
		  flex-shrink: 0;
		  flex-wrap: wrap;
		}
		
		#toolbar h1 { font-size: 13px; color: rgb(30 55 140); margin-right: 4px; letter-spacing: .02em; }
		
		.btn {
		  padding: 3px 9px;
		  border: 1px solid rgb(155 150 105);
		  background: rgb(200 197 156);
		  color: rgb(0 0 0);
		  cursor: pointer;
		  border-radius: 4px;
		  font-size: 12px;
		  font-family: inherit;
		  transition: background .1s;
		}
		[data-filter] { min-width: 52px; text-align: center; }
		.btn:hover  { background: rgb(180 176 132); }
		.btn.active { background: rgb(50 70 150); border-color: rgb(30 55 140); color: rgb(255 255 255); }
		
		.filter-ALL   { color: rgb(0 0 0); }
		.filter-TOOL  { color: rgb(20 100 40); }
		.filter-FAIL  { color: rgb(180 60 0); }
		.filter-ERROR { color: rgb(160 0 0); }
		.filter-START { color: rgb(30 55 140); }
		.filter-STOP  { color: rgb(90 85 55); }
		
		label.chk {
		  display: flex;
		  align-items: center;
		  gap: 4px;
		  cursor: pointer;
		  font-size: 12px;
		  user-select: none;
		}
		
		#sep, .sep-thin { width: 1px; height: 18px; background: rgb(160 155 110); margin: 0 2px; }
		
		#status { margin-left: auto; font-size: 11px; color: rgb(90 85 55); white-space: nowrap; }
		#status.live { color: rgb(20 100 40); }
		#status.err  { color: rgb(160 0 0); }
		
		/* ── Log area ── */
		#log { flex: 1; overflow-y: scroll; padding: 2px 0; }
		
		#empty { padding: 16px 12px; color: rgb(110 105 70); font-style: italic; }
		
		/* ── Entries ── */
		.entry {
		  padding: 1px 12px;
		  display: grid;
		  grid-template-columns: 12ch 5ch 1fr;
		  gap: 10px;
		  white-space: pre-wrap;
		  word-break: break-all;
		  border-bottom: 1px solid transparent;
		}
		.entry:hover { background: rgb(200 197 156); }
		
		.ts  { color: rgb(110 105 70); }
		.lvl { font-weight: 600; }
		.msg { color: rgb(0 0 0); }
		
		.lvl-START { color: rgb(30 55 140); }
		.lvl-STOP  { color: rgb(110 105 70); }
		.lvl-TOOL  { color: rgb(20 100 40); }
		.lvl-ERROR { color: rgb(160 0 0); }
		.lvl-OTHER { color: rgb(110 105 70); }

		/* ERROR rows get a subtle left border */
		.entry[data-level="ERROR"] { border-left: 2px solid rgb(160 0 0); }

		/* TOOL message sub-fields */
		.ws-msb  { color: rgb(20 100 40);  font-weight: 700; cursor: help; }
		.ws-adh  { color: rgb(170 100 0);  font-weight: 700; cursor: help; }
		.ws-unk  { color: rgb(110 105 70); cursor: help; }
		.tool-name { color: rgb(0 0 0); font-weight: 600; }
		.tool-ms   { color: rgb(110 105 70); }
		.tool-ok   { color: rgb(20 100 40); font-weight: 600; }
		.tool-err  { color: rgb(160 0 0);   font-weight: 600; }
		.tool-subj { color: rgb(60 55 30); }
		.tool-det  { color: rgb(80 75 45);  font-style: italic; }
		
		.hidden { display: none !important; }

		/* ── Search bar ── */
		#searchBar {
		  padding: 5px 12px;
		  background: rgb(196 193 150);
		  border-bottom: 1px solid rgb(160 155 110);
		  display: flex;
		  gap: 8px;
		  align-items: center;
		  flex-shrink: 0;
		}
		#searchInput {
		  flex: 1;
		  padding: 3px 8px;
		  border: 1px solid rgb(155 150 105);
		  border-radius: 4px;
		  font-family: inherit;
		  font-size: 12px;
		  background: rgb(230 228 200);
		}
		#searchInput:focus { outline: 2px solid rgb(50 70 150); }
		#searchCount { font-size: 11px; color: rgb(90 85 55); white-space: nowrap; }
		.entry.highlight { background: rgb(255 255 180) !important; }

		/* ── Legend ── */
		#legend {
		  padding: 4px 12px;
		  background: rgb(210 207 170);
		  border-bottom: 1px solid rgb(160 155 110);
		  font-size: 11px;
		  color: rgb(70 65 35);
		  flex-shrink: 0;
		}
		kbd {
		  background: rgb(230 228 200);
		  border: 1px solid rgb(160 155 110);
		  border-radius: 3px;
		  padding: 1px 4px;
		  font-family: inherit;
		  font-size: 10px;
		}
	  </style>
	</head>
	<body>
	  <div id="toolbar">
		<h1>RoslynMcp Logs</h1>
		<button class="btn filter-ALL   active" data-filter="ALL"  >ALL</button>
		<button class="btn filter-TOOL  "       data-filter="TOOL" >TOOL</button>
		<button class="btn filter-FAIL  "       data-filter="FAIL" >FAIL</button>
		<div class="sep-thin"></div>
		<button class="btn filter-ERROR "       data-filter="ERROR">ERROR</button>
		<button class="btn filter-START "       data-filter="START">START</button>
		<button class="btn filter-STOP  "       data-filter="STOP" >STOP</button>
		<div id="sep"></div>
		<label class="chk"><input type="checkbox" id="autoScroll" checked> Auto-scroll</label>
		<button class="btn" id="clearBtn">Clear</button>
		<button class="btn" id="shutdownBtn">Shutdown</button>
		<button class="btn" id="helpBtn" title="Keyboard shortcuts">?</button>
		<span id="status">Connecting…</span>
	  </div>
	  <div id="legend" class="hidden">
		<kbd>1</kbd> ALL <kbd>2</kbd> TOOL <kbd>3</kbd> FAIL <kbd>4</kbd> ERROR <kbd>5</kbd> START <kbd>6</kbd> STOP &nbsp;|&nbsp; <kbd>S</kbd> Auto-scroll &nbsp; <kbd>Ctrl+F</kbd> Search &nbsp; <kbd>Ctrl+L</kbd> Clear &nbsp; <kbd>Esc</kbd> Close/Jump to end &nbsp; <kbd>Q</kbd> Shutdown &nbsp; <kbd>?</kbd> This legend
	  </div>
	  <div id="searchBar" class="hidden">
		<input type="text" id="searchInput" placeholder="Search logs… (Esc to close)">
		<span id="searchCount"></span>
	  </div>
	  <div id="log"><div id="empty">Waiting for log entries…</div></div>
	  
	  <script>
		const logEl      = document.getElementById('log');
		const statusEl   = document.getElementById('status');
		const autoScroll = document.getElementById('autoScroll');
		const emptyEl    = document.getElementById('empty');
		let   filter       = 'ALL';
		let   count        = 0;
		let   scrolledUp   = false;  // true when user has scrolled away from bottom
		let   hasSelection = false;  // true while text is selected in the log
		
		// ── Smart scroll tracking ─────────────────────────────────────────────
		// Mark scrolledUp when user leaves the bottom; clear it when they return.
		logEl.addEventListener('scroll', () => {
		  scrolledUp = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight > 4;
		}, { passive: true });
		
		// Pause while text is selected; resume (if at bottom) once cleared.
		document.addEventListener('selectionchange', () => {
		  hasSelection = (window.getSelection()?.toString().length ?? 0) > 0;
		});
		
		function shouldScroll() {
		  return autoScroll.checked && !scrolledUp && !hasSelection;
		}
		
		// ── Filters ──────────────────────────────────────────────────────────
		document.querySelectorAll('[data-filter]').forEach(btn => {
		  btn.addEventListener('click', () => {
			document.querySelectorAll('[data-filter]').forEach(b => b.classList.remove('active'));
			btn.classList.add('active');
			filter = btn.dataset.filter;
			document.querySelectorAll('.entry').forEach(el => {
			  if(filter === 'FAIL')
				el.classList.toggle('hidden', el.dataset.fail !== 'true');
			  else
				el.classList.toggle('hidden', filter !== 'ALL' && el.dataset.level !== filter);
			});
		  });
		});
		
		// ── Shutdown ──────────────────────────────────────────────────────────
		document.getElementById('shutdownBtn').addEventListener('click', () => {
		  fetch('/shutdown', { method: 'POST' }).catch(() => {});
		});
		
		// ── Clear ─────────────────────────────────────────────────────────────
		document.getElementById('clearBtn').addEventListener('click', () => {
		  document.querySelectorAll('.entry').forEach(el => el.remove());
		  emptyEl.style.display = '';
		  count = 0;
		  updateStatus();
		});
		
		// ── Append entry ──────────────────────────────────────────────────────
		function addEntry(e) {
		  emptyEl.style.display = 'none';
		  const level  = e.Level || 'OTHER';
		  const isFail = level === 'TOOL' && e.Success === false;
		  const div    = document.createElement('div');
		  div.className     = 'entry';
		  div.dataset.level = level;
		  if(isFail) div.dataset.fail = 'true';

		  if(filter === 'FAIL' ? !isFail : (filter !== 'ALL' && level !== filter))
			div.classList.add('hidden');

		  const ts  = document.createElement('span');
		  ts.className   = 'ts';
		  ts.textContent = e.Timestamp;

		  const lvl = document.createElement('span');
		  lvl.className   = `lvl lvl-${level}`;
		  lvl.textContent = level;

		  const msg = document.createElement('span');
		  msg.className = 'msg';

		  if(level === 'TOOL' && e.ToolName) {
			const wsHints = { MSB: 'MSBuildWorkspace (.csproj)', ADH: 'AdhocWorkspace (no .csproj)', '---': 'Workspace mode unknown' };

			if(e.WorkspaceMode) {
			  const ws = document.createElement('span');
			  ws.className = e.WorkspaceMode === 'MSB' ? 'ws-msb' : e.WorkspaceMode === 'ADH' ? 'ws-adh' : 'ws-unk';
			  ws.textContent = e.WorkspaceMode + ' ';
			  ws.title = wsHints[e.WorkspaceMode] || '';
			  msg.appendChild(ws);
			}

			const ok = document.createElement('span');
			ok.className   = e.Success ? 'tool-ok' : 'tool-err';
			ok.textContent = (e.Success ? 'OK ' : 'ERR') + ' ';
			msg.appendChild(ok);

			const ms = document.createElement('span');
			ms.className   = 'tool-ms';
			ms.textContent = `${String(e.ElapsedMs).padStart(5)}ms `;
			msg.appendChild(ms);

			const tn = document.createElement('span');
			tn.className   = 'tool-name';
			tn.textContent = e.ToolName;
			msg.appendChild(tn);

			if(e.Subject) {
			  const subj = document.createElement('span');
			  subj.className   = 'tool-subj';
			  subj.textContent = ' ' + e.Subject;
			  msg.appendChild(subj);
			}

			if(e.Detail) {
			  const det = document.createElement('span');
			  det.className   = 'tool-det';
			  det.textContent = ' — ' + e.Detail;
			  msg.appendChild(det);
			}
		  } else {
			msg.textContent = e.Message || e.Raw;
		  }

		  div.appendChild(ts);
		  div.appendChild(lvl);
		  div.appendChild(msg);
		  logEl.appendChild(div);

		  count++;
		  updateStatus();
		  
		  if(shouldScroll())
			logEl.scrollTop = logEl.scrollHeight;
		}
		
		function updateStatus() {
		  const base = `${count} entr${count === 1 ? 'y' : 'ies'}`;
		  statusEl.textContent = connected ? `${base} (live)` : base;
		}
		
		// ── SSE connection ────────────────────────────────────────────────────
		let connected = false;
		
		function connect() {
		  const es = new EventSource('/logs/stream');
		  
		  es.onopen = () => {
			connected = true;
			statusEl.className = 'live';
			updateStatus();
		  };
		  
		  es.onmessage = e => {
			try { addEntry(JSON.parse(e.data)); }
			catch { /* malformed entry — skip */ }
		  };
		  
		  es.onerror = () => {
			connected = false;
			statusEl.className = 'err';
			statusEl.textContent = 'Disconnected — reconnecting in 3s…';
			es.close();
			setTimeout(connect, 3000);
		  };
		}
		
		// ── Help legend toggle ────────────────────────────────────────────
		document.getElementById('helpBtn').addEventListener('click', () => {
		  document.getElementById('legend').classList.toggle('hidden');
		});

		// ── Keyboard shortcuts ────────────────────────────────────────────
		const searchBar   = document.getElementById('searchBar');
		const searchInput = document.getElementById('searchInput');
		const searchCount = document.getElementById('searchCount');
		let   searching   = false;

		function clearSearch() {
		  searchInput.value = '';
		  searchCount.textContent = '';
		  document.querySelectorAll('.entry.highlight').forEach(el => el.classList.remove('highlight'));
		  searchBar.classList.add('hidden');
		  searching = false;
		}

		function doSearch() {
		  const q = searchInput.value.toLowerCase().trim();
		  document.querySelectorAll('.entry.highlight').forEach(el => el.classList.remove('highlight'));
		  if(!q) { searchCount.textContent = ''; return; }
		  let n = 0;
		  document.querySelectorAll('.entry').forEach(el => {
			if(el.textContent.toLowerCase().includes(q)) { el.classList.add('highlight'); n++; }
		  });
		  searchCount.textContent = `${n} match${n !== 1 ? 'es' : ''}`;
		}

		searchInput.addEventListener('input', doSearch);

		function clickFilter(name) {
		  const btn = document.querySelector(`[data-filter="${name}"]`);
		  if(btn) btn.click();
		}

		document.addEventListener('keydown', e => {
		  // Ctrl+L — clear log
		  if(e.ctrlKey && e.key === 'l') {
			e.preventDefault();
			document.getElementById('clearBtn').click();
			return;
		  }

		  // Ctrl+F — open search
		  if(e.ctrlKey && e.key === 'f') {
			e.preventDefault();
			searchBar.classList.remove('hidden');
			searching = true;
			searchInput.focus();
			searchInput.select();
			return;
		  }

		  // Ctrl+C — copy selected entry text (let browser handle if text is selected)
		  // (browser default handles this — no override needed)

		  // Esc — close search, clear selection, or jump to end
		  if(e.key === 'Escape') {
			if(searching) { clearSearch(); return; }
			const sel = window.getSelection();
			if(sel && sel.toString().length > 0) { sel.removeAllRanges(); return; }
			logEl.scrollTop = logEl.scrollHeight;
			scrolledUp = false;
			return;
		  }

		  // 1-6 — filter shortcuts, ? — legend (only when not in search input)
		  if(!searching && !e.ctrlKey && !e.altKey && !e.metaKey) {
			switch(e.key) {
			  case '1': clickFilter('ALL');   break;
			  case '2': clickFilter('TOOL');  break;
			  case '3': clickFilter('FAIL');  break;
			  case '4': clickFilter('ERROR'); break;
			  case '5': clickFilter('START'); break;
			  case '6': clickFilter('STOP');  break;
			  case 's': autoScroll.checked = !autoScroll.checked; break;
			  case 'q': document.getElementById('shutdownBtn').click(); break;
			  case '?': document.getElementById('legend').classList.toggle('hidden'); break;
			}
		  }
		});

		connect();
	  </script>
	</body>
	</html>
	""";
}
