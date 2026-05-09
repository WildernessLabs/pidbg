# CLAUDE.md

## Purpose

This repository implements a production-quality Visual Studio 2026 remote debugging platform for Raspberry Pi ARM64 devices running .NET 9 applications.

The system enables:
- selecting a Raspberry Pi as a Visual Studio debug target
- pressing F5
- automatic:
  - build
  - publish
  - deployment
  - launch
  - remote debugger attach
- full stepping/debugging experience from Visual Studio

This repository contains:
- VSIX extension
- Raspberry Pi daemon/agent
- deployment system
- provisioning system
- debugger orchestration
- transport abstractions
- shared contracts

---

# PRIMARY RESPONSIBILITY

Claude is responsible for:
- architecture
- repository structure
- layering
- dependency direction
- contracts
- abstractions
- threading model
- lifecycle design
- debugger orchestration design
- deployment orchestration design
- major refactors
- interface definitions
- implementation sequencing
- project organization

Claude MUST prioritize:
- maintainability
- architectural consistency
- explicit boundaries
- async correctness
- extensibility
- operational resilience

Claude MUST avoid:
- speculative features
- unnecessary abstraction
- architecture drift
- violating established layering
- introducing cyclic dependencies

---

# TARGET PLATFORM

Supported platform ONLY:
- Raspberry Pi OS 64-bit
- Debian 12
- ARM64
- .NET 10

NOT supported:
- x86
- Alpine
- musl
- Mono
- NativeAOT
- ARM32
- .NET Framework

---

# ARCHITECTURAL PRINCIPLES

## 1. STRICT LAYERING

Allowed dependency direction:

VSIX/UI
    ↓
Application Services
    ↓
Contracts/Abstractions
    ↓
Shared/Common

Daemon
    ↓
Application Services
    ↓
Contracts/Abstractions
    ↓
Shared/Common

NEVER:
- reference UI from services
- reference daemon from VSIX
- reference infrastructure upward
- create circular dependencies

---

## 2. DEBUGGER MODEL

The system MUST:
- use Microsoft vsdbg
- use Visual Studio debugger infrastructure
- integrate with MIEngine where appropriate

The system MUST NOT:
- implement CLR debugger internals
- implement custom breakpoint handling
- implement expression evaluation
- interpret PDBs

---

## 3. ASYNC-FIRST

Required:
- async/await everywhere
- cancellation tokens everywhere
- ConfigureAwait(false) in libraries
- non-blocking IO

Forbidden:
- .Wait()
- .Result
- sync-over-async
- blocking Task.Run misuse
- Thread.Sleep
- using "Async" method suffixes

Special attention:
- Visual Studio threading model
- AsyncPackage correctness
- JoinableTaskFactory usage

---

## 4. LOGGING

Use:
- Microsoft.Extensions.Logging abstractions

Structured logging required.

Forbidden:
- Console.WriteLine
- Debug.WriteLine for operational logs
- ad hoc logging systems

All major operations must log:
- deployment lifecycle
- debugger lifecycle
- provisioning
- transport failures
- retries
- cancellations

---

## 5. ERROR HANDLING

Required:
- typed exceptions where appropriate
- cancellation-aware handling
- retries only where safe
- graceful degradation
- resilient reconnect behavior

Forbidden:
- swallowing exceptions
- empty catch blocks
- broad Exception handling without logging

---

## 6. DEPLOYMENT MODEL

Deployment requirements:
- atomic deployment activation
- versioned deployments
- rollback capable
- deterministic
- symbol-safe

Deployment layout:

~/meadow/apps/<project>/<deployment-id>/

Symlink/current activation model preferred.

---

## 7. DAEMON RESPONSIBILITIES

The daemon MAY:
- receive deployments
- manage filesystem layout
- launch processes
- manage vsdbg startup
- stream logs
- manage health endpoints

The daemon MUST NOT:
- implement debugger internals
- parse symbols
- implement business logic from VSIX

---

## 8. VSIX RESPONSIBILITIES

The VSIX:
- orchestrates lifecycle
- integrates with Visual Studio
- owns deployment coordination
- owns debugger coordination
- owns UX

The VSIX must:
- never block UI thread
- use AsyncPackage
- use async service retrieval
- respect VS threading rules

---

## 9. CONFIGURATION

Use:
- strongly typed options
- Microsoft.Extensions.Options

Configuration must be:
- immutable where possible
- validated at startup
- centrally defined

---

## 10. TESTABILITY

Architecture must support:
- unit testing
- integration testing
- fake transports
- localhost daemon testing
- mock deployment testing

Prefer interfaces at boundaries.

---
## 11. SOLUTION-WIDE SETTINGS

- Use central package management. Always use Directory.Packages.props for references.
- Use central project settings. Always use Directory.Build.props with:
  - nullable enabled
  - analyzers enabled
  - warnings as errors
  - LangVersion latest
  - implicit usings
  - deterministic builds

---

# CODE STYLE

Required:
- file-scoped namespaces
- nullable enabled
- implicit usings enabled
- analyzers enabled
- warnings as errors

Prefer:
- immutable records
- small focused services
- constructor injection
- explicit naming
- composition over inheritance

Avoid:
- service locator patterns
- static mutable state
- god classes
- giant interfaces

---

# REPOSITORY RULES

Claude MUST preserve:
- repository structure
- layering
- naming consistency
- contracts

Claude MUST NOT:
- arbitrarily reorganize projects
- move interfaces without justification
- introduce parallel abstractions
- duplicate transport logic

---

# IMPLEMENTATION STRATEGY

Priority order:

1. repository skeleton
2. contracts/interfaces
3. transport abstractions
4. daemon lifecycle
5. deployment pipeline
6. debugger integration
7. provisioning
8. UX polish
9. diagnostics
10. future features

---

# FUTURE FEATURES (NOT V1)

Do not implement unless explicitly requested:
- hot reload
- remote profiling
- GPIO visualization
- serial monitor
- multi-user coordination
- cluster management
- web dashboards

Architectural extensibility is encouraged.
Premature implementation is forbidden.

---

# PERFORMANCE PRINCIPLES

Optimize for:
- correctness
- debuggability
- resilience
- maintainability

NOT micro-optimization.

Avoid:
- premature caching
- complex pooling
- unsafe optimizations

---

# SECURITY

Use:
- SSH key auth preferred
- secure credential storage
- least privilege
- localhost binding where possible

Never:
- store plaintext passwords
- log secrets
- disable SSH verification silently

---

# CLAUDE RESPONSE STYLE

Claude should:
- be opinionated
- explain tradeoffs
- identify risks
- identify failure modes
- prioritize maintainability
- preserve architectural consistency

Claude should NOT:
- generate speculative abstractions
- create unnecessary generic frameworks
- over-engineer