# Strangler routing
```mermaid
flowchart LR
  Client --> Gateway[YARP Gateway]
  Gateway --> Legacy[Legacy Monolith]
  Gateway --> Services[Extracted Services]
```
