import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  insecureSkipTLSVerify: true,
  stages: [
    { duration: '10s', target: 5 },   // Ramp up to 5 concurrent users
    { duration: '30s', target: 15 },  // Peak load: 15 concurrent users
    { duration: '10s', target: 0 },   // Ramp down
  ],
};

const BASE_URL = 'https://localhost';

export default function () {
  const username = `k6_user_${__VU}_${__ITER}_${Date.now()}`;
  const password = 'Password123!';

  // 1. Реєстрація
  const regRes = http.post(`${BASE_URL}/api/auth/register`, JSON.stringify({
    username: username,
    password: password,
  }), { headers: { 'Content-Type': 'application/json' } });

  check(regRes, { 'Зареєстровано': (r) => r.status === 200 });

  // 2. Вхід
  const loginRes = http.post(`${BASE_URL}/api/auth/login`, JSON.stringify({
    username: username,
    password: password,
  }), { headers: { 'Content-Type': 'application/json' } });

  const token = loginRes.json('token');
  check(loginRes, { 'Успішний вхід': (r) => r.status === 200 && token !== undefined });

  // 3. Створення задачі обчислення
  const taskRes = http.post(`${BASE_URL}/api/tasks`, JSON.stringify({
    type: 0,
    width: 300,
    height: 200,
    maxIterations: 80,
    centerX: -0.7,
    centerY: 0.0,
    zoom: 1.0,
    palette: 0,
    smooth: true,
  }), {
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`,
    },
  });

  check(taskRes, {
    'Задачу прийнято (202)': (r) => r.status === 202,
    'Присутній заголовок X-Served-By': (r) => r.headers['X-Served-By'] !== undefined,
  });

  sleep(1);
}
