import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

export const options = {
  scenarios: { bounded_reads: { executor: 'constant-vus', vus: 5, duration: '1m' } },
};

const base = (__ENV.TARGET_BASE_URL || '').replace(/\/$/, '');
if (!base) throw new Error('TARGET_BASE_URL is required');
const catalogLatency = new Trend('catalog_read_ms');
const inventoryLatency = new Trend('inventory_read_ms');
const orderLatency = new Trend('order_read_ms');

export default function () {
  const health = http.get(base + '/health/ready');
  check(health, { 'gateway ready': (response) => response.status === 200 });

  const catalog = http.get(base + '/api/products');
  catalogLatency.add(catalog.timings.duration);
  check(catalog, { 'catalog read succeeds': (response) => response.status === 200 });

  if (__ENV.PRODUCT_ID) {
    const inventory = http.get(base + '/api/inventory/' + __ENV.PRODUCT_ID);
    inventoryLatency.add(inventory.timings.duration);
    check(inventory, { 'inventory read succeeds': (response) => response.status === 200 });
  }
  if (__ENV.ORDER_ID) {
    const order = http.get(base + '/api/orders/' + __ENV.ORDER_ID);
    orderLatency.add(order.timings.duration);
    check(order, { 'order read succeeds': (response) => response.status === 200 });
  }
  sleep(1);
}