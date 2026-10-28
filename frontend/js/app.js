// Main Application Script (ES Module)

let currentUser = null;
let currentTaskId = null;
let currentEventSource = null;
let authMode = 'login';
const MAX_COST_LIMIT = 500_000_000;

document.addEventListener('DOMContentLoaded', async () => {
  window.openAuthModal = openAuthModal;
  window.closeAuthModal = closeAuthModal;
  window.submitAuth = submitAuth;
  window.logout = logout;
  window.switchTab = switchTab;
  window.onTypeChanged = onTypeChanged;
  window.updateCostMeter = updateCostMeter;
  window.startCalculation = startCalculation;
  window.cancelCurrentTask = cancelCurrentTask;
  window.onCanvasClick = onCanvasClick;
  window.resetView = resetView;
  window.downloadImage = downloadImage;
  window.loadHistory = loadHistory;
  window.repeatTask = repeatTask;

  updateCostMeter();
  await checkAuth();
});

async function checkAuth() {
  try {
    const res = await fetch('/api/auth/me');
    if (res.ok) {
      currentUser = await res.json();
      renderAuthNav();
      loadHistory();
    } else {
      renderAuthNav();
    }
  } catch {
    renderAuthNav();
  }
}

function renderAuthNav() {
  const userInfo = document.getElementById('userInfo');
  const adminLink = document.getElementById('adminLink');
  const loginBtn = document.getElementById('loginBtn');
  const registerBtn = document.getElementById('registerBtn');
  const logoutBtn = document.getElementById('logoutBtn');

  if (currentUser) {
    userInfo.style.display = 'inline-block';
    userInfo.textContent = `Користувач: ${currentUser.username}`;
    loginBtn.style.display = 'none';
    registerBtn.style.display = 'none';
    logoutBtn.style.display = 'inline-block';

    if (currentUser.role === 'admin') {
      adminLink.style.display = 'inline-block';
    } else {
      adminLink.style.display = 'none';
    }
  } else {
    userInfo.style.display = 'none';
    adminLink.style.display = 'none';
    loginBtn.style.display = 'inline-block';
    registerBtn.style.display = 'inline-block';
    logoutBtn.style.display = 'none';
  }
}

function openAuthModal(mode) {
  authMode = mode;
  document.getElementById('modalTitle').textContent = mode === 'login' ? 'Вхід у систему' : 'Реєстрація користувача';
  document.getElementById('modalSubmitBtn').textContent = mode === 'login' ? 'Увійти' : 'Зареєструватися';
  document.getElementById('modalError').style.display = 'none';
  document.getElementById('authModal').style.display = 'flex';
}

function closeAuthModal() {
  document.getElementById('authModal').style.display = 'none';
}

async function submitAuth() {
  const username = document.getElementById('modalUsername').value.trim();
  const password = document.getElementById('modalPassword').value;
  const errorDiv = document.getElementById('modalError');

  if (!username || !password) {
    errorDiv.textContent = "Заповніть усі поля.";
    errorDiv.style.display = 'block';
    return;
  }

  const endpoint = authMode === 'login' ? '/api/auth/login' : '/api/auth/register';

  try {
    const res = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password })
    });

    const data = await res.json();
    if (res.ok) {
      closeAuthModal();
      currentUser = { userId: data.userId, username: data.username, role: data.role };
      renderAuthNav();
      loadHistory();
    } else {
      errorDiv.textContent = data.message || "Помилка авторизації.";
      errorDiv.style.display = 'block';
    }
  } catch (err) {
    errorDiv.textContent = "Помилка зв'язку із сервером.";
    errorDiv.style.display = 'block';
  }
}

async function logout() {
  await fetch('/api/auth/logout', { method: 'POST' });
  currentUser = null;
  renderAuthNav();
  switchTab('workspace');
}

function switchTab(tab) {
  const tabWs = document.getElementById('tabWorkspace');
  const tabHist = document.getElementById('tabHistory');
  const btns = document.querySelectorAll('.tab-btn');

  if (tab === 'workspace') {
    tabWs.style.display = 'block';
    tabHist.style.display = 'none';
    btns[0].classList.add('active');
    btns[1].classList.remove('active');
  } else {
    tabWs.style.display = 'none';
    tabHist.style.display = 'block';
    btns[0].classList.remove('active');
    btns[1].classList.add('active');
    loadHistory();
  }
}

function onTypeChanged() {
  const type = document.getElementById('fractalType').value;
  const juliaParams = document.getElementById('juliaParams');
  if (type === "1") { // Julia
    juliaParams.style.display = 'block';
    document.getElementById('centerX').value = "0.0";
    document.getElementById('centerY').value = "0.0";
  } else {
    juliaParams.style.display = 'none';
    document.getElementById('centerX').value = "-0.7";
    document.getElementById('centerY').value = "0.0";
  }
  updateCostMeter();
}

function updateCostMeter() {
  const width = parseInt(document.getElementById('imgWidth').value) || 0;
  const height = parseInt(document.getElementById('imgHeight').value) || 0;
  const iter = parseInt(document.getElementById('maxIter').value) || 0;
  const startBtn = document.getElementById('startBtn');
  const costMeter = document.getElementById('costMeter');
  const costValue = document.getElementById('costValue');
  const statusText = document.getElementById('costStatusText');

  const cost = width * height * iter;
  costValue.textContent = cost.toLocaleString('uk-UA');

  costMeter.className = 'cost-meter';

  if (cost > MAX_COST_LIMIT || width > 3000 || height > 3000 || iter > 5000 || width < 100 || height < 100) {
    costMeter.classList.add('cost-red');
    statusText.textContent = "Перевищено допустимий ліміт! Зменшіть роздільність або кількість ітерацій.";
    startBtn.disabled = true;
  } else if (cost > 200_000_000) {
    costMeter.classList.add('cost-yellow');
    statusText.textContent = "Важка задача. Обчислення може тривати від кількох десятків секунд.";
    startBtn.disabled = false;
  } else {
    costMeter.classList.add('cost-green');
    statusText.textContent = "Трудомісткість у межах норми.";
    startBtn.disabled = false;
  }
}

async function startCalculation() {
  if (!currentUser) {
    openAuthModal('login');
    return;
  }

  const payload = {
    type: parseInt(document.getElementById('fractalType').value),
    width: parseInt(document.getElementById('imgWidth').value),
    height: parseInt(document.getElementById('imgHeight').value),
    maxIterations: parseInt(document.getElementById('maxIter').value),
    centerX: parseFloat(document.getElementById('centerX').value),
    centerY: parseFloat(document.getElementById('centerY').value),
    zoom: parseFloat(document.getElementById('zoom').value),
    cRe: parseFloat(document.getElementById('cRe').value),
    cIm: parseFloat(document.getElementById('cIm').value),
    palette: parseInt(document.getElementById('palette').value),
    smooth: document.getElementById('smooth').checked
  };

  const canvas = document.getElementById('fractalCanvas');
  canvas.width = payload.width;
  canvas.height = payload.height;
  document.getElementById('canvasPlaceholder').style.display = 'none';

  try {
    const res = await fetch('/api/tasks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    const data = await res.json();
    if (!res.ok) {
      alert(`Помилка: ${data.message || (data.errors ? data.errors.join('\n') : "Не вдалося створити задачу.")}`);
      return;
    }

    currentTaskId = data.id;
    showProgressBanner(data);
    listenToEvents(currentTaskId);
  } catch (err) {
    alert("Помилка відправки запиту на сервер.");
  }
}

function showProgressBanner(data) {
  const banner = document.getElementById('activeProgressBanner');
  banner.style.display = 'block';
  document.getElementById('bannerStage').textContent = data.stage || "В черзі";
  document.getElementById('bannerEta').textContent = data.estimatedWait ? `(Очікування: ${data.estimatedWait})` : '';
  document.getElementById('bannerProgressBar').style.width = '0%';
  document.getElementById('bannerProgressText').textContent = '0%';
  document.getElementById('bannerServerId').textContent = 'Сервер: очікування захоплення...';
  document.getElementById('cancelTaskBtn').style.display = 'inline-block';
}

function listenToEvents(taskId) {
  if (currentEventSource) {
    currentEventSource.close();
  }

  currentEventSource = new EventSource(`/api/tasks/${taskId}/events`);

  currentEventSource.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data);
      updateProgressDisplay(data);

      if (data.status === 'Success') {
        currentEventSource.close();
        displayFinishedImage(taskId);
      } else if (data.status === 'Failed' || data.status === 'Cancelled') {
        currentEventSource.close();
      }
    } catch (e) {
      console.error(e);
    }
  };

  currentEventSource.onerror = () => {
    // Falls back automatically or closes when complete
  };
}

function updateProgressDisplay(data) {
  document.getElementById('bannerStage').textContent = data.stage;
  document.getElementById('bannerProgressBar').style.width = `${data.progress}%`;
  document.getElementById('bannerProgressText').textContent = `${data.progress}%`;

  if (data.appServerId) {
    document.getElementById('bannerServerId').textContent = `Обчислює сервер: ${data.appServerId}`;
  } else if (data.queuePosition) {
    document.getElementById('bannerServerId').textContent = `Позиція в черзі: ${data.queuePosition}`;
  }

  if (data.estimatedWait) {
    document.getElementById('bannerEta').textContent = `(Очікування: ${data.estimatedWait})`;
  } else {
    document.getElementById('bannerEta').textContent = '';
  }

  if (data.status === 'Cancelled') {
    document.getElementById('cancelTaskBtn').style.display = 'none';
    document.getElementById('bannerStage').textContent = "Скасовано";
  } else if (data.status === 'Failed') {
    document.getElementById('cancelTaskBtn').style.display = 'none';
    document.getElementById('bannerStage').textContent = "Помилка обчислення";
  }
}

async function cancelCurrentTask() {
  if (!currentTaskId) return;

  if (!confirm("Ви впевнені, що хочете скасувати виконання цієї задачі?")) return;

  try {
    await fetch(`/api/tasks/${currentTaskId}/cancel`, { method: 'POST' });
  } catch (err) {
    alert("Помилка скасування задачі.");
  }
}

function displayFinishedImage(taskId) {
  const canvas = document.getElementById('fractalCanvas');
  const ctx = canvas.getContext('2d');
  const img = new Image();

  img.onload = () => {
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
    document.getElementById('downloadBtn').style.display = 'inline-block';
    document.getElementById('cancelTaskBtn').style.display = 'none';
  };
  img.src = `/api/tasks/${taskId}/image?t=${Date.now()}`;
}

function onCanvasClick(event) {
  const canvas = document.getElementById('fractalCanvas');
  const rect = canvas.getBoundingClientRect();
  const clickX = event.clientX - rect.left;
  const clickY = event.clientY - rect.top;

  const width = canvas.width;
  const height = canvas.height;

  const currentCenterX = parseFloat(document.getElementById('centerX').value);
  const currentCenterY = parseFloat(document.getElementById('centerY').value);
  const currentZoom = parseFloat(document.getElementById('zoom').value);

  const aspectRatio = width / height;
  const rangeY = 2.0 / currentZoom;
  const rangeX = rangeY * aspectRatio;

  const minX = currentCenterX - rangeX / 2.0;
  const maxY = currentCenterY + rangeY / 2.0;

  // New center in mathematical coordinates
  const newCenterX = minX + (clickX / rect.width) * rangeX;
  const newCenterY = maxY - (clickY / rect.height) * rangeY;
  const newZoom = currentZoom * 2.5;

  document.getElementById('centerX').value = newCenterX.toFixed(6);
  document.getElementById('centerY').value = newCenterY.toFixed(6);
  document.getElementById('zoom').value = newZoom.toFixed(2);

  startCalculation();
}

function resetView() {
  const type = document.getElementById('fractalType').value;
  if (type === "1") {
    document.getElementById('centerX').value = "0.0";
    document.getElementById('centerY').value = "0.0";
  } else {
    document.getElementById('centerX').value = "-0.7";
    document.getElementById('centerY').value = "0.0";
  }
  document.getElementById('zoom').value = "1.0";
  updateCostMeter();
}

function downloadImage() {
  if (!currentTaskId) return;
  window.open(`/api/tasks/${currentTaskId}/image`, '_blank');
}

async function loadHistory() {
  if (!currentUser) return;

  const filter = document.getElementById('historyFilter').value;
  const grid = document.getElementById('historyGrid');
  grid.innerHTML = '<div style="color: var(--text-muted);">Завантаження історії...</div>';

  try {
    const url = filter ? `/api/tasks?status=${encodeURIComponent(filter)}` : '/api/tasks';
    const res = await fetch(url);
    const data = await res.json();

    if (!data.items || data.items.length === 0) {
      grid.innerHTML = '<div style="color: var(--text-muted);">Немає збережених задач.</div>';
      return;
    }

    grid.innerHTML = data.items.map(item => `
      <div class="history-card">
        ${item.hasThumb
          ? `<img src="/api/tasks/${item.Id}/thumb" alt="Мініатюра фрактала">`
          : `<div style="height: 160px; display: flex; align-items: center; justify-content: center; background: #111; color: var(--text-muted);">Немає зображення</div>`}
        <div class="history-card-body">
          <div style="display: flex; justify-content: space-between; align-items: center;">
            <strong>${item.Type}</strong>
            <span class="badge badge-${item.Status.toLowerCase()}">${item.Stage}</span>
          </div>
          <div>Сервер: ${item.AppServerId || '—'}</div>
          <div>Створено: ${new Date(item.CreatedAt).toLocaleString('uk-UA')}</div>
          <div style="margin-top: 0.5rem; display: flex; gap: 0.5rem;">
            <button class="btn btn-secondary" style="flex: 1;" onclick="repeatTask('${item.Id}')">Повторити</button>
            ${item.hasImage ? `<a href="/api/tasks/${item.Id}/image" target="_blank" class="btn btn-primary" style="text-decoration: none; padding: 0.35rem 0.6rem;">PNG</a>` : ''}
          </div>
        </div>
      </div>
    `).join('');
  } catch (err) {
    grid.innerHTML = '<div style="color: var(--danger);">Помилка завантаження історії.</div>';
  }
}

async function repeatTask(taskId) {
  try {
    const res = await fetch(`/api/tasks/${taskId}`);
    const data = await res.json();
    if (data.paramsData) {
      const p = data.paramsData;
      document.getElementById('fractalType').value = p.Type;
      document.getElementById('imgWidth').value = p.Width;
      document.getElementById('imgHeight').value = p.Height;
      document.getElementById('maxIter').value = p.MaxIterations;
      document.getElementById('centerX').value = p.CenterX;
      document.getElementById('centerY').value = p.CenterY;
      document.getElementById('zoom').value = p.Zoom;
      document.getElementById('palette').value = p.Palette;
      document.getElementById('smooth').checked = p.Smooth;

      if (p.Type === 1) {
        document.getElementById('juliaParams').style.display = 'block';
        document.getElementById('cRe').value = p.CRe;
        document.getElementById('cIm').value = p.CIm;
      }

      switchTab('workspace');
      updateCostMeter();
    }
  } catch (err) {
    alert("Не вдалося завантажити параметри задачі.");
  }
}
