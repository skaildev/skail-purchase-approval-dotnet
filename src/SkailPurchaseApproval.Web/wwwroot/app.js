const KEY = 'purchase-approval.orderId';
const LEVELS = ['manager', 'director'];
const LEVEL_LABELS = { manager: 'Manager', director: 'Director' };
const STATUS_LABELS = { NotRequested: 'Not requested', Pending: 'Pending', Approved: 'Approved', Rejected: 'Rejected', NoResponse: 'No response' };
const PHASE_LABELS = { Starting: 'Starting…', WaitingForManager: 'Waiting for Manager', WaitingForDirector: 'Waiting for Director' };

const $ = (id) => document.getElementById(id);
const els = {
  supplier: $('supplier'), description: $('description'), amount: $('amount'), start: $('start'),
  state: $('state'), error: $('error'), result: $('result'), timeline: $('timeline'),
  orderId: $('orderId'), monitor: $('monitor'), deadlines: $('deadlines'),
};
const cards = Object.fromEntries(LEVELS.map((l) => {
  const root = document.querySelector(`[data-level="${l}"]`);
  const part = (role) => root.querySelector(`[data-role="${role}"]`);
  return [l, { root, status: part('status'), countdown: part('countdown'), detail: part('detail'), by: part('by'), reason: part('reason'), approve: part('approve'), reject: part('reject') }];
}));

let order = null;
let source = null;

const isTerminal = (o) => o && ['Approved', 'Rejected', 'Cancelled', 'Failed'].includes(o.phase);
const time = (iso) => new Date(iso).toLocaleTimeString();

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message ?? '';
}

// The countdown is only visual: the real deadline is the durable Delay of the function.
function remainingSeconds(view) {
  if (!view?.requestedAt) return null;
  return Math.max(0, Math.ceil(view.deadlineSeconds - (Date.now() - new Date(view.requestedAt).getTime()) / 1000));
}

function renderCountdowns() {
  for (const level of LEVELS) {
    const view = order?.[level];
    const seconds = order?.active && view?.status === 'Pending' ? remainingSeconds(view) : null;
    cards[level].countdown.textContent = seconds === null ? '' : seconds > 0 ? `Deadline in ${seconds}s` : 'Deadline reached…';
  }
}

function render() {
  const active = order?.active === true;
  for (const input of [els.supplier, els.description, els.amount, els.start]) input.disabled = active;
  if (order) {
    els.supplier.value = order.purchase.supplier;
    els.description.value = order.purchase.description;
    els.amount.value = order.purchase.amount;
  }

  els.state.textContent = order ? (PHASE_LABELS[order.phase] ?? order.phase) : 'Idle';
  els.state.className = 'badge ' + (order?.phase === 'Failed' || order?.phase === 'Cancelled' || order?.phase === 'Rejected' ? 'failed'
    : order?.phase === 'Approved' ? 'done' : order ? 'active' : 'idle');
  els.orderId.textContent = order?.orderId ?? '-';
  showError(order?.error);

  for (const level of LEVELS) {
    const card = cards[level];
    const view = order?.[level];
    const status = view?.status ?? 'NotRequested';
    card.status.textContent = STATUS_LABELS[status];
    card.status.className = `pill ${status}`;
    const who = view?.by ? `${view.by}${view.at ? ' · ' + time(view.at) : ''}` : '';
    card.detail.textContent = [who, view?.reason ? `“${view.reason}”` : ''].filter(Boolean).join(' · ');
    // only the pending level of an active order can still decide
    const canDecide = active && status === 'Pending';
    for (const control of [card.by, card.reason, card.approve, card.reject]) control.disabled = !canDecide;
  }
  renderCountdowns();

  const r = order?.result;
  els.result.hidden = !r;
  els.result.className = 'result ' + (r?.end === 'Approved' ? 'ok' : 'bad');
  if (r?.end === 'Approved') els.result.textContent = `Approved by the ${LEVEL_LABELS[r.decision?.level]} · reference ${r.reference}`;
  else if (r?.end === 'Rejected') els.result.textContent = `Rejected by the ${LEVEL_LABELS[r.decision?.level]}${r.reason ? ` (${r.reason})` : ''}`;
  else if (r) els.result.textContent = 'Cancelled · no response from the Manager or the Director';

  els.timeline.replaceChildren(...(order?.timeline ?? []).map((t) => {
    const item = document.createElement('li');
    item.textContent = `${time(t.at)}  ${t.text}`;
    return item;
  }));
}

function connect(orderId) {
  source?.close();
  source = new EventSource(`/api/orders/${orderId}/stream`);
  source.onmessage = (event) => {
    order = JSON.parse(event.data);
    render();
    if (isTerminal(order)) {
      source.close();
      localStorage.removeItem(KEY);
    }
  };
  // EventSource reconnects by itself; the stream sends the current snapshot first
}

async function start() {
  for (const input of [els.supplier, els.description, els.amount, els.start]) input.disabled = true;
  showError(null);
  try {
    const res = await fetch('/api/orders', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        supplier: els.supplier.value,
        description: els.description.value,
        amount: els.amount.value === '' ? null : Number(els.amount.value),
      }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
    order = await res.json();
    // a new order starts with clean decision fields (they keep the previous order's text otherwise)
    for (const level of LEVELS) { cards[level].by.value = ''; cards[level].reason.value = ''; }
    localStorage.setItem(KEY, order.orderId);
    render();
    connect(order.orderId);
  } catch (e) {
    showError(`Could not start: ${e.message}`);
    render();
  }
}

async function decide(level, approved) {
  const card = cards[level];
  for (const control of [card.by, card.reason, card.approve, card.reject]) control.disabled = true;
  showError(null);
  try {
    const res = await fetch(`/api/orders/${order.orderId}/decision`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ level, approved, by: card.by.value, reason: card.reason.value }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
  } catch (e) {
    showError(`Could not send the decision: ${e.message}`); // the server released the level: the buttons come back
  }
  render();
}

els.start.addEventListener('click', start);
for (const level of LEVELS) {
  cards[level].approve.addEventListener('click', () => decide(level, true));
  cards[level].reject.addEventListener('click', () => decide(level, false));
}

fetch('/api/config').then((r) => r.json()).then((c) => {
  els.supplier.placeholder = c.defaultSupplier;
  els.description.placeholder = c.defaultDescription;
  els.amount.placeholder = String(c.defaultAmount);
  if (!order) {
    els.supplier.value = c.defaultSupplier;
    els.description.value = c.defaultDescription;
    els.amount.value = c.defaultAmount;
  }
  els.deadlines.textContent = `Deadlines: Manager ${c.managerDeadlineSeconds}s · Director ${c.directorDeadlineSeconds}s`;
  if (c.monitorUrl) { els.monitor.href = c.monitorUrl; els.monitor.hidden = false; }
}).catch(() => {});

setInterval(renderCountdowns, 1000);

// reload keeps the cards, the countdown and the locked controls right: the state always comes from the server
const saved = localStorage.getItem(KEY);
if (saved) {
  fetch(`/api/orders/${saved}`).then((r) => (r.ok ? r.json() : null)).then((o) => {
    if (!o) return localStorage.removeItem(KEY);
    order = o;
    render();
    if (!isTerminal(o)) connect(o.orderId);
  });
}
render();
