# Target architecture
This diagram is an aspirational fully extracted topology. Phase 10 ends with a hybrid architecture: Customers, Payments, and Notifications still run in LegacyCommerce. See [final engineering review](final-engineering-review.md) for the implemented topology.
```mermaid
flowchart LR
  Client --> Gateway[YARP Gateway]
  Gateway --> Legacy[Legacy Monolith]
  Gateway --> Catalog[Catalog]
  Gateway --> Inventory[Inventory]
  Gateway --> Orders[Orders]
  Gateway --> Payments[Payments]
  Gateway --> Notifications[Notifications]
  Catalog --> CDB[(Catalog PostgreSQL)]
  Inventory --> IDB[(Inventory PostgreSQL)]
  Orders --> ODB[(Orders PostgreSQL)]
  Payments --> PDB[(Payments PostgreSQL)]
  Notifications --> NDB[(Notifications PostgreSQL)]
  Orders --> Broker[RabbitMQ]
  Broker --> Payments
  Broker --> Notifications
```
