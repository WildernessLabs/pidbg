# GEMINI.md

## Purpose

This repository implements a Visual Studio 2026 remote debugging system for Raspberry Pi ARM64 devices using .NET 9.

Gemini is primarily responsible for:
- implementation
- scaffolding
- plumbing
- API integration
- repetitive engineering work
- incremental feature completion

Gemini is NOT responsible for:
- architecture redesign
- repository restructuring
- changing layering
- changing contracts
- changing transport model
- changing debugger model

If architecture appears problematic:
- STOP
- explain the concern
- propose alternatives
- do NOT autonomously redesign the system

---

# CRITICAL RULES

## 1. DO NOT CHANGE ARCHITECTURE

You MUST:
- preserve repository structure
- preserve project boundaries
- preserve dependency direction
- preserve interfaces/contracts

You MUST NOT:
- move projects
- merge layers
- create circular dependencies
- introduce alternate architectures
- replace established abstractions

---

## 2. DO NOT INVENT FRAMEWORKS

Forbidden:
- custom dependency injection containers
- custom logging systems
- custom threading systems
- custom transport stacks
- custom serialization systems

Use:
- Microsoft.Extensions.*
- ASP.NET Core primitives
- standard .NET patterns

---

## 3. DEBUGGER CONSTRAINTS

The system MUST:
- use Microsoft vsdbg
- use Visual Studio debugger infrastructure

The system MUST NOT:
- implement a custom debugger
- interpret PDBs
- implement CLR debugging logic
- replace MIEngine

Never attempt to:
- emulate breakpoints
- emulate stepping
- emulate watch evaluation

---

## 4. ASYNC RULES

Required:
- async/await everywhere
- cancellation tokens everywhere
- async IO only

Forbidden:
- .Result
- .Wait()
- Thread.Sleep
- blocking collections
- sync-over-async
- using "Async" method suffixes

Visual Studio integration code MUST:
- avoid UI thread blocking
- use JoinableTaskFactory correctly
- respect AsyncPackage threading rules

---

## 5. IMPLEMENTATION SCOPE

Gemini should implement:
- services
- DTOs
- transport plumbing
- gRPC services
- deployment logic
- process management
- provisioning logic
- filesystem management
- configuration binding
- logging integration

Gemini should NOT autonomously redesign:
- debugger transport
- VSIX architecture
- lifecycle sequencing
- repository organization

---

# CODE QUALITY RULES

Required:
- nullable enabled
- analyzers clean
- warnings as errors
- structured logging
- cancellation support
- deterministic behavior

Avoid:
- giant methods
- giant classes
- deep inheritance
- hidden side effects

Prefer:
- small focused services
- constructor injection
- immutable DTOs
- explicit naming

---

# LOGGING RULES

Use:
- ILogger<T>

All operational flows must log:
- deployment start/end
- retries
- failures
- cancellations
- debugger startup
- process launch
- provisioning actions

Never:
- log secrets
- log passwords
- use Console.WriteLine

---

# DEPLOYMENT RULES

Deployment must:
- be atomic
- be versioned
- support rollback
- preserve symbol integrity

Deployment directories:

~/meadow/apps/<project>/<deployment-id>/

Never:
- overwrite active deployment in-place
- delete active deployment unexpectedly

---

# DAEMON RULES

The daemon may:
- receive packages
- unpack deployments
- launch processes
- manage vsdbg startup
- stream logs

The daemon must NOT:
- implement debugger logic
- interpret symbols
- manage Visual Studio UI concerns

---

# VSIX RULES

VSIX code must:
- use AsyncPackage
- use async APIs only
- avoid blocking UI thread
- use dependency injection

Never:
- block the main thread
- use synchronous service retrieval
- perform long-running work on UI thread

---

# SECURITY RULES

Never:
- hardcode credentials
- log secrets
- bypass SSH validation silently
- expose daemon publicly by default

Prefer:
- SSH keys
- localhost binding
- least privilege

---

# TESTING RULES

All major functionality should include:
- unit tests
- integration tests where practical

Important test areas:
- deployment activation
- rollback
- process cleanup
- cancellation
- retry behavior
- reconnect behavior

---

# BEFORE MAKING CHANGES

Before major changes:
1. verify dependency direction
2. verify layering
3. verify interfaces
4. verify threading implications
5. verify cancellation flow

If uncertain:
- explain uncertainty
- ask for clarification
- avoid speculative redesign

---

# IMPLEMENTATION STYLE

Gemini should:
- follow existing patterns
- preserve consistency
- minimize architectural churn
- produce incremental commits
- explain tradeoffs briefly

Gemini should NOT:
- create alternate abstractions
- introduce parallel patterns
- refactor unrelated code
- "improve" architecture without request

---

# PERFORMANCE RULES

Optimize for:
- correctness
- maintainability
- resilience
- debuggability

Avoid premature optimization.

---

# FUTURE FEATURES

Do NOT implement unless explicitly requested:
- hot reload
- GPIO visualization
- serial console
- remote profiler
- web dashboard
- cluster/device discovery

Architect for extensibility only.