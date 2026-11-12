import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

export const options = {
  scenarios: { bounded_orders: { executor: 'constant-vus', vus: 3, duration: '30s' } },
};

if (__ENV.ISOLATED_TEST_ENV !== 'true' || __ENV.ALLOW_WRITES !== 'true')
  throw new Error('An explicit isolated test environment and write opt-in are required');
for (const key of ['TARGET_BASE_URL', 'CUSTOMER_ID', 'PRODUCT_ID'])
  if (!__ENV[key]) throw new Error(key + ' is required');

const base = __ENV.TARGET_BASE_URL.replace(/\/$/, '');
const submissionLatency = new Trend('order_submission_ms');
const sagaCompletion = new Trend('saga_completion_ms');

export default function () {
  const suffix = (Date.now() + __VU * 100000 + __ITER).toString(16).padStart(12, '0').slice(-12);
  const id = '00000000-0000-4000-8000-' + suffix;
  const headers = { 'Content-Type': 'application/json', 'Idempotency-Key': id };
  const started = Date.now();
  const submitted = http.post(base + '/api/orders',
    JSON.stringify({ customerId: __ENV.CUSTOMER_ID, productId: __ENV.PRODUCT_ID, quantity: 1 }),
    { headers });
  submissionLatency.add(submitted.timings.duration);
  if (!check(submitted, { 'order accepted': (response) => response.status === 201 })) return;

  const orderId = submitted.json('id');
  const payment = http.post(base + '/api/orders/' + orderId + '/payment',
    JSON.stringify({ succeed: true }), { headers: { 'Content-Type': 'application/json' } });
  if (!check(payment, { 'payment intent accepted': (response) => response.status === 202 })) return;

  for (let attempt = 0; attempt < 30; attempt++) {
    const order = http.get(base + '/api/orders/' + orderId);
    if (order.status === 200 && ['Confirmed', 'Cancelled', 'Failed'].includes(order.json('status'))) {
      sagaCompletion.add(Date.now() - started);
      check(order, { 'order confirmed': (response) => response.json('status') === 'Confirmed' });
      return;
    }
    sleep(0.2);
  }
  check({ completed: false }, { 'saga completed within six seconds': (result) => result.completed });
}