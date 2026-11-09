import http from 'k6/http';
import { check, fail } from 'k6';

export const options = {
  vus: 1,
  duration: '10s',
  thresholds: {
    http_req_duration: ['p(95)<200', 'avg<100'],
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99']
  }
};

const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:8080';

export function setup() {
  const code = 'CR-B-' + (Date.now() % 10000000);
  const res = http.post(`${BASE_URL}/courses`, JSON.stringify({
    code: code,
    price: 400000,
    capacity: 100,
    level: 1
  }), {
    headers: { 'Content-Type': 'application/json' }
  });
  if (res.status !== 201) {
    fail('Setup course creation failed: ' + res.body);
  }
  return { courseId: res.json('id') };
}

export default function (data) {
  const suffix = __VU + '-' + __ITER + '-' + (Date.now() % 10000000);
  const studentPayload = JSON.stringify({
    code: 'S-' + suffix,
    age: 20,
    entryScore: 85,
    regular: false
  });

  const studentRes = http.post(`${BASE_URL}/students`, studentPayload, {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'POST /students' }
  });
  check(studentRes, {
    'Student created 201': (r) => r.status === 201
  });
  if (studentRes.status !== 201) {
    fail('Student creation failed');
  }
  const studentId = studentRes.json('id');

  const enrollPayload = JSON.stringify({
    reference: 'R-' + suffix,
    studentId: studentId,
    courseId: data.courseId
  });

  const enrollRes = http.post(`${BASE_URL}/enrollments`, enrollPayload, {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'POST /enrollments' }
  });
  check(enrollRes, {
    'Enrollment created 201': (r) => r.status === 201,
    'Prepayment calculated': (r) => r.json('prepaid') === 100000
  });
  if (enrollRes.status !== 201) {
    fail('Enrollment creation failed');
  }
  const enrollmentId = enrollRes.json('id');

  const getRes = http.get(`${BASE_URL}/enrollments/${enrollmentId}`, {
    tags: { name: 'GET /enrollments/{id}' }
  });
  check(getRes, {
    'Enrollment retrieved 200': (r) => r.status === 200,
    'Status is Enrolled': (r) => r.json('status') === 'Enrolled'
  });

  const cancelRes = http.post(`${BASE_URL}/enrollments/${enrollmentId}/cancel`, null, {
    tags: { name: 'POST /enrollments/{id}/cancel' }
  });
  check(cancelRes, {
    'Enrollment cancelled 200': (r) => r.status === 200,
    'Status is Cancelled': (r) => r.json('status') === 'Cancelled',
    'Refund equals prepaid': (r) => r.json('refund') === 100000
  });
}
