'use strict';

// --- Global state ---
let apiUrl = 'http://localhost:5000';
let captchaProvider = '';
let captchaSiteKey = '';
let jwtToken = sessionStorage.getItem('jwt') || null;

// --- Bootstrap ---
async function boot() {
  try {
    const res = await fetch('/config.json');
    const cfg = await res.json();
    apiUrl = cfg.apiUrl || apiUrl;
  } catch (_) { /* use default */ }

  try {
    const res = await fetch(`${apiUrl}/api/config`);
    if (res.ok) {
      const cfg = await res.json();
      captchaProvider = cfg.captchaProvider || '';
      captchaSiteKey  = cfg.captchaSiteKey  || '';
      loadCaptchaScript();
    }
  } catch (_) { /* captcha disabled */ }

  renderNav();
  window.addEventListener('hashchange', route);
  route();
}

function loadCaptchaScript() {
  if (!captchaSiteKey) return;
  if (captchaProvider === 'turnstile') {
    if (!document.querySelector('script[src*="turnstile"]')) {
      const s = document.createElement('script');
      s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js';
      s.async = true;
      document.head.appendChild(s);
    }
  } else if (captchaProvider === 'recaptcha') {
    if (!document.querySelector('script[src*="recaptcha"]')) {
      const s = document.createElement('script');
      s.src = `https://www.google.com/recaptcha/api.js?render=${captchaSiteKey}`;
      s.async = true;
      document.head.appendChild(s);
    }
  }
}

// --- Nav ---
function renderNav() {
  const nav = document.getElementById('nav-links');
  if (jwtToken) {
    nav.innerHTML = `
      <a href="#change-password">Change Password</a>
      <a href="#" id="logout-btn">Logout</a>`;
    document.getElementById('logout-btn').addEventListener('click', e => {
      e.preventDefault();
      sessionStorage.removeItem('jwt');
      jwtToken = null;
      renderNav();
      navigate('#leaderboard');
    });
  } else {
    nav.innerHTML = `
      <a href="#leaderboard">Leaderboard</a>
      <a href="#register">Register</a>
      <a href="#login">Login</a>`;
  }
}

// --- Router ---
function navigate(hash) {
  window.location.hash = hash;
}

function route() {
  const hash = window.location.hash || '#leaderboard';
  const app = document.getElementById('app');

  if (hash === '#leaderboard' || hash === '') {
    renderLeaderboard(app);
  } else if (hash === '#register') {
    if (jwtToken) { navigate('#leaderboard'); return; }
    renderRegister(app);
  } else if (hash === '#login') {
    if (jwtToken) { navigate('#leaderboard'); return; }
    renderLogin(app);
  } else if (hash === '#change-password') {
    if (!jwtToken) { navigate('#login'); return; }
    renderChangePassword(app);
  } else {
    navigate('#leaderboard');
  }
}

// --- API helpers ---
async function apiFetch(path, opts = {}) {
  const headers = { 'Content-Type': 'application/json', ...(opts.headers || {}) };
  if (jwtToken) headers['Authorization'] = `Bearer ${jwtToken}`;
  const res = await fetch(`${apiUrl}${path}`, { ...opts, headers });
  return res;
}

// --- Leaderboard ---
const CLASS_NAMES = {
  0: 'Unknown', 1: 'Archer', 2: 'Cleric', 3: 'Fighter',
  4: 'Joker', 5: 'Mage', 6: 'Shaman'
};
const RACE_NAMES = { 0: 'Human', 1: 'Elf', 2: 'Tuskar' };

async function renderLeaderboard(container) {
  container.innerHTML = '<h1>Leaderboard</h1><div class="loading">Loading...</div>';
  try {
    const res = await apiFetch('/api/leaderboard');
    if (!res.ok) throw new Error('Failed to load leaderboard');
    const rows = await res.json();

    const tbody = rows.map((r, i) => `
      <tr class="${i < 3 ? 'rank-' + (i + 1) : ''}">
        <td>${i + 1}</td>
        <td>${esc(r.name)}</td>
        <td>${r.level}</td>
        <td>${r.exp.toLocaleString()}</td>
        <td>${CLASS_NAMES[r.class] || r.class}</td>
        <td>${RACE_NAMES[r.race] || r.race}</td>
      </tr>`).join('');

    container.innerHTML = `
      <h1>Leaderboard</h1>
      <table>
        <thead><tr>
          <th>#</th><th>Name</th><th>Level</th>
          <th>Exp</th><th>Class</th><th>Race</th>
        </tr></thead>
        <tbody>${tbody || '<tr><td colspan="6">No characters yet.</td></tr>'}</tbody>
      </table>`;
  } catch (err) {
    container.innerHTML = `<h1>Leaderboard</h1><p class="error-msg">${esc(err.message)}</p>`;
  }
}

// --- Register ---
function renderRegister(container) {
  container.innerHTML = `
    <h1>Register</h1>
    <div class="card">
      <form id="register-form">
        <div class="form-group">
          <label>Username</label>
          <input type="text" id="reg-username" required autocomplete="username" />
        </div>
        <div class="form-group">
          <label>Web Password <span style="color:#666;font-size:.8em">(for this site)</span></label>
          <input type="password" id="reg-webpw" required autocomplete="new-password" />
        </div>
        <div class="form-group">
          <label>In-game Password</label>
          <input type="password" id="reg-ingamepw" required autocomplete="new-password" />
        </div>
        <div class="form-group">
          <label>Email <span style="color:#666;font-size:.8em">(optional)</span></label>
          <input type="email" id="reg-email" autocomplete="email" />
        </div>
        ${captchaWidget('reg-captcha')}
        <button type="submit" id="reg-btn">Create Account</button>
        <div id="reg-msg"></div>
      </form>
    </div>`;

  initCaptchaWidget('reg-captcha');

  document.getElementById('register-form').addEventListener('submit', async e => {
    e.preventDefault();
    const btn = document.getElementById('reg-btn');
    btn.disabled = true;
    const msg = document.getElementById('reg-msg');
    msg.innerHTML = '';

    const captchaToken = getCaptchaToken('reg-captcha');

    try {
      const res = await apiFetch('/api/accounts', {
        method: 'POST',
        body: JSON.stringify({
          username: document.getElementById('reg-username').value,
          webPassword: document.getElementById('reg-webpw').value,
          ingamePassword: document.getElementById('reg-ingamepw').value,
          email: document.getElementById('reg-email').value || null,
          captchaToken
        })
      });
      if (res.status === 201) {
        msg.innerHTML = '<span class="success-msg">Account created! You can now log in.</span>';
        document.getElementById('register-form').reset();
        resetCaptcha('reg-captcha');
      } else if (res.status === 409) {
        msg.innerHTML = '<span class="error-msg">Username already taken.</span>';
        resetCaptcha('reg-captcha');
      } else if (res.status === 429) {
        msg.innerHTML = '<span class="error-msg">Too many attempts. Try again later.</span>';
      } else {
        const body = await res.json().catch(() => ({}));
        msg.innerHTML = `<span class="error-msg">${esc(body.error || 'Registration failed.')}</span>`;
        resetCaptcha('reg-captcha');
      }
    } catch (err) {
      msg.innerHTML = `<span class="error-msg">Network error.</span>`;
    } finally {
      btn.disabled = false;
    }
  });
}

// --- Login ---
function renderLogin(container, requiresCaptcha = false) {
  container.innerHTML = `
    <h1>Login</h1>
    <div class="card">
      <form id="login-form">
        <div class="form-group">
          <label>Username</label>
          <input type="text" id="login-username" required autocomplete="username" />
        </div>
        <div class="form-group">
          <label>Password</label>
          <input type="password" id="login-pw" required autocomplete="current-password" />
        </div>
        ${requiresCaptcha ? captchaWidget('login-captcha') : ''}
        <button type="submit" id="login-btn">Login</button>
        <div id="login-msg"></div>
      </form>
    </div>`;

  if (requiresCaptcha) initCaptchaWidget('login-captcha');

  const form = document.getElementById('login-form');
  let showedCaptcha = requiresCaptcha;

  form.addEventListener('submit', async e => {
    e.preventDefault();
    const btn = document.getElementById('login-btn');
    btn.disabled = true;
    const msg = document.getElementById('login-msg');
    msg.innerHTML = '';

    const captchaToken = showedCaptcha ? getCaptchaToken('login-captcha') : null;

    try {
      const res = await apiFetch('/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({
          username: document.getElementById('login-username').value,
          password: document.getElementById('login-pw').value,
          captchaToken
        })
      });

      if (res.ok) {
        const body = await res.json();
        jwtToken = body.token;
        sessionStorage.setItem('jwt', jwtToken);
        renderNav();
        navigate('#leaderboard');
      } else {
        const body = await res.json().catch(() => ({}));
        msg.innerHTML = `<span class="error-msg">${esc(body.error || 'Invalid credentials.')}</span>`;
        if (body.requiresCaptcha && !showedCaptcha) {
          showedCaptcha = true;
          // Inject captcha widget
          const captchaDiv = document.createElement('div');
          captchaDiv.className = 'captcha-wrap';
          captchaDiv.id = 'login-captcha';
          btn.parentElement.insertBefore(captchaDiv, btn);
          initCaptchaWidget('login-captcha');
        } else {
          resetCaptcha('login-captcha');
        }
      }
    } catch (err) {
      msg.innerHTML = `<span class="error-msg">Network error.</span>`;
    } finally {
      btn.disabled = false;
    }
  });
}

// --- Change Password ---
function renderChangePassword(container) {
  container.innerHTML = `
    <h1>Change Password</h1>
    <div style="display:flex;gap:2rem;flex-wrap:wrap;">
      <div class="card">
        <h2>Web Password</h2>
        <form id="webpw-form">
          <div class="form-group">
            <label>New Web Password</label>
            <input type="password" id="new-webpw" required autocomplete="new-password" />
          </div>
          <div class="form-group">
            <label>Confirm</label>
            <input type="password" id="confirm-webpw" required autocomplete="new-password" />
          </div>
          <button type="submit" id="webpw-btn">Update</button>
          <div id="webpw-msg"></div>
        </form>
      </div>
      <div class="card">
        <h2>In-game Password</h2>
        <form id="ingamepw-form">
          <div class="form-group">
            <label>New In-game Password</label>
            <input type="password" id="new-ingamepw" required autocomplete="new-password" />
          </div>
          <button type="submit" id="ingamepw-btn">Update</button>
          <div id="ingamepw-msg"></div>
        </form>
      </div>
    </div>`;

  document.getElementById('webpw-form').addEventListener('submit', async e => {
    e.preventDefault();
    const btn = document.getElementById('webpw-btn');
    const msg = document.getElementById('webpw-msg');
    const pw  = document.getElementById('new-webpw').value;
    const cpw = document.getElementById('confirm-webpw').value;
    msg.innerHTML = '';
    if (pw !== cpw) { msg.innerHTML = '<span class="error-msg">Passwords do not match.</span>'; return; }
    btn.disabled = true;
    try {
      const res = await apiFetch('/api/auth/set-web-password', {
        method: 'POST',
        body: JSON.stringify({ newPassword: pw })
      });
      if (res.status === 204) {
        msg.innerHTML = '<span class="success-msg">Web password updated.</span>';
        document.getElementById('webpw-form').reset();
      } else {
        msg.innerHTML = '<span class="error-msg">Failed to update.</span>';
      }
    } catch (_) {
      msg.innerHTML = '<span class="error-msg">Network error.</span>';
    } finally { btn.disabled = false; }
  });

  document.getElementById('ingamepw-form').addEventListener('submit', async e => {
    e.preventDefault();
    const btn = document.getElementById('ingamepw-btn');
    const msg = document.getElementById('ingamepw-msg');
    btn.disabled = true;
    try {
      const res = await apiFetch('/api/auth/set-ingame-password', {
        method: 'POST',
        body: JSON.stringify({ newPassword: document.getElementById('new-ingamepw').value })
      });
      if (res.status === 204) {
        msg.innerHTML = '<span class="success-msg">In-game password updated.</span>';
        document.getElementById('ingamepw-form').reset();
      } else {
        msg.innerHTML = '<span class="error-msg">Failed to update.</span>';
      }
    } catch (_) {
      msg.innerHTML = '<span class="error-msg">Network error.</span>';
    } finally { btn.disabled = false; }
  });
}

// --- Captcha helpers ---
function captchaWidget(id) {
  if (!captchaSiteKey) return '';
  if (captchaProvider === 'turnstile') {
    return `<div class="captcha-wrap cf-turnstile" id="${id}" data-sitekey="${esc(captchaSiteKey)}"></div>`;
  }
  if (captchaProvider === 'recaptcha') {
    return `<div class="captcha-wrap g-recaptcha" id="${id}" data-sitekey="${esc(captchaSiteKey)}"></div>`;
  }
  return '';
}

function initCaptchaWidget(id) {
  // Turnstile auto-renders from data-sitekey; reCAPTCHA also auto-renders.
  // Nothing extra needed for auto-render mode.
}

function getCaptchaToken(id) {
  if (!captchaSiteKey) return null;
  const el = document.getElementById(id);
  if (!el) return null;
  // Turnstile
  if (captchaProvider === 'turnstile') {
    const input = el.querySelector('[name="cf-turnstile-response"]');
    return input ? input.value : null;
  }
  // reCAPTCHA
  if (captchaProvider === 'recaptcha') {
    const input = el.querySelector('[name="g-recaptcha-response"]');
    return input ? input.value : null;
  }
  return null;
}

function resetCaptcha(id) {
  if (!captchaSiteKey) return;
  const el = document.getElementById(id);
  if (!el) return;
  try {
    if (captchaProvider === 'turnstile' && window.turnstile) {
      window.turnstile.reset(el);
    } else if (captchaProvider === 'recaptcha' && window.grecaptcha) {
      window.grecaptcha.reset();
    }
  } catch (_) { /* widget not ready yet */ }
}

// --- Utilities ---
function esc(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// --- Start ---
boot();
