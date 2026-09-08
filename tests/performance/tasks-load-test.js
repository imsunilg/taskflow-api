// k6 load test for the task-list endpoint.
//   Seed first:  k6 run -e SEED=50000 tasks-load-test.js   (runs the seed stage once)
//   Then load:   k6 run tasks-load-test.js
//
// Gate: p95 latency for GET /api/tasks?page=1&pageSize=25 must stay under 500ms
// (PRD §8 / §25).

import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost:5095';
const EMAIL = __ENV.EMAIL || 'demo@taskflow.app';
const PASSWORD = __ENV.PASSWORD || 'password123';
const SEED = Number(__ENV.SEED || 0);

export const options = SEED
  ? { vus: 10, iterations: SEED }
  : {
      scenarios: {
        list: { executor: 'constant-vus', vus: 10, duration: '30s' },
      },
      thresholds: {
        'http_req_duration{name:list}': ['p(95)<500'],
      },
    };

function login() {
  const res = http.post(`${BASE}/api/auth/login`, JSON.stringify({ email: EMAIL, password: PASSWORD }), {
    headers: { 'Content-Type': 'application/json' },
  });
  return res.json('data.accessToken');
}

const token = login();
const authHeaders = { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } };

export default function () {
  if (SEED) {
    const priorities = ['Low', 'Medium', 'High', 'Urgent'];
    http.post(
      `${BASE}/api/tasks`,
      JSON.stringify({
        title: `Perf task ${__ITER}`,
        priority: priorities[__ITER % 4],
        dueDate: '2026-10-01',
      }),
      authHeaders,
    );
    return;
  }

  const res = http.get(`${BASE}/api/tasks?page=1&pageSize=25`, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { name: 'list' },
  });
  check(res, {
    'status 200': (r) => r.status === 200,
    'under 500ms': (r) => r.timings.duration < 500,
  });
  sleep(0.2);
}
