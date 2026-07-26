# Architecture Decision Records

English | [简体中文](./README.zh-CN.md)

ADRs record durable design choices for `tripo-revit`. They are the change-control
surface for trust boundaries, front doors, and credential ownership. Update the
ADR (or supersede it) before contradicting implementation.

| ID | Title | Status |
| --- | --- | --- |
| [0001](./0001-dual-front-doors-and-sidecar-credentials.md) | Dual front doors (host UI + MCP) and sidecar-only API credentials | Accepted; Rhino/Grasshopper clauses are compatibility context only |

Related non-ADR design locks:

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — current trust boundaries and stage sequence
- [`../SECURITY.md`](../SECURITY.md) — credential, journal, bridge, and artifact rules
- [`../MATERIALS-DESIGN.md`](../MATERIALS-DESIGN.md) — OBJ baked-diffuse materials and import modes

The English body of each ADR is normative. A Chinese summary inside an ADR is
provided for reading convenience only.
