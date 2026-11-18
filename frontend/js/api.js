const STATUS_UA = {
  Queued: 'В черзі',
  Running: 'Рахується',
  Success: 'Готово',
  Failed: 'Помилка',
  Cancelled: 'Скасовано'
};

async function api(path, method = 'GET', body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }
  try {
    const res = await fetch(path, options);
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = { message: text }; }
    return { ok: res.ok, status: res.status, data };
  } catch {
    return { ok: false, status: 0, data: { message: "Немає зв'язку з сервером." } };
  }
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function formatWait(seconds) {
  if (seconds < 60) return `~${seconds} с`;
  return `~${Math.floor(seconds / 60)} хв ${seconds % 60} с`;
}

function formatTime(iso) {
  return iso ? new Date(iso).toLocaleTimeString('uk-UA') : '';
}

async function logout() {
  await api('/api/auth/logout', 'POST');
  location.href = '/login.html';
}
