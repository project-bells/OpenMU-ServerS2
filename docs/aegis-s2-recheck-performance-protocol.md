# Aegis-Mu S2 Recheck and Performance Optimization Protocol

## Purpose and pass criteria

This protocol audits the Season 6 Episode 3 OpenMU engine after it has been reduced to an Aegis-Mu Season 2 content profile. It is deliberately based on this repository's architecture, not on a traditional MuEMU/IGC GameServer.

The target is not merely hidden UI. A disabled feature passes only when:

1. it has no initialized configuration graph reachable from `GameConfiguration`;
2. it has no active feature, periodic-task, packet-handler, or view plug-in;
3. it cannot create a map, timer, event loop, item, skill, or persistence row;
4. a forged packet reaches a constant-time rejection path without exceptions or per-packet logs;
5. no login query materializes data which exists solely for the disabled feature;
6. server and client agree on every retained numeric ID and wire layout;
7. an idle-versus-disabled-feature benchmark shows no additional timer callbacks, tasks, allocations, database statements, or retained objects.

Record the exact server and client commit SHA with every test report. Never compare performance across Debug and Release builds.

## 1. Establish a measurable baseline

### 1.1 Build and runtime mode

- Build `Release`, Tiered PGO enabled, Server GC enabled for a dedicated multi-core server.
- Disable debugger attachment and verbose packet logging.
- Run one process per test server instance; pinning processor affinity is optional but must be identical between baselines.
- Warm for 10 minutes before capturing a 15-minute idle trace and a 15-minute active trace.
- Use the same database snapshot, bot script, map, monster count and client build for every A/B run.

Capture at minimum:

```powershell
dotnet-counters monitor -p <PID> `
  System.Runtime `
  Microsoft.AspNetCore.Hosting `
  Npgsql

dotnet-trace collect -p <PID> --duration 00:05:00 `
  --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime
```

Relevant `System.Runtime` counters are CPU usage, working set, allocation rate, Gen 0/1/2 collections, GC heap size, LOH size, time in GC, thread-pool thread count, queue length, completed work items, monitor lock contention and exception count. Add OpenTelemetry export for OpenMU's `MUnique.OpenMU.GameLogic` meter; `GameContext` already exposes player/map counters and `MiniGameManager` exposes `MiniGameCount`.

Release gates:

- idle `MiniGameCount == 0` when no retained event is active;
- no steadily increasing thread-pool queue, heap, connection count or handler count;
- exception rate is zero during a valid-client soak;
- rejected-packet flood produces no exception and no unbounded log volume;
- after warm-up, disabled-feature types have zero live instances except static metadata proven unavoidable by compilation.

Use `dotnet-gcdump` or `dotnet-dump gcroot` to prove reachability. Searching source is not proof of zero live objects.

## 2. Server background-process eradication

### 2.1 Understand the actual timer model

`src/GameLogic/GameContext.cs` creates two shared timers:

- `_recoverTimer`, which regenerates active players;
- `_tasksTimer`, which calls the active `IPeriodicTaskPlugIn` chain once per second.

Do not disable these shared timers merely because S6 features are removed; retained systems use them. Instead, ensure no disabled feature implements an active `IPeriodicTaskPlugIn`. The correct zero-overhead state is that the disabled plug-in is absent from the plug-in manager, so the shared dispatch never calls it.

Audit command:

```powershell
rg -n "IPeriodicTaskPlugIn|Timer\(|PeriodicTimer|Task\.Run|EnsureGameLoopRunning" `
  src/GameLogic src/GameServer src/Persistence/Initialization
```

For each hit record owner, creation point, cancellation/disposal path and feature flag. A `return` inside a callback fails this audit because the callback, task wake-up and delegate dispatch still exist.

### 2.2 Gens

Relevant source families:

- `src/GameLogic/Gens/`
- `src/GameLogic/PlayerActions/Gens/`
- `src/GameServer/MessageHandler/Gens/`
- `src/GameServer/RemoteView/Gens/`
- `src/Persistence/Initialization/VersionSeasonSix/Items/GensRewardItems.cs`
- Gens sections in `VersionSeasonSix/NpcInitialization.cs` and `GameConfigurationInitializer.cs`

Gens is predominantly event/plugin driven, not a dedicated background thread. For true removal:

1. Do not create `GensConfiguration`, ranks, rewards, NPCs or Gens item definitions.
2. Mark every Gens feature and relationship plug-in inactive in the persisted plug-in configuration. Prefer removing them from the Aegis initializer so a fresh database cannot activate them.
3. Do not register `GensGroupHandlerPlugIn` or its subhandlers. Removing only subhandlers still allocates the group container per player.
4. Do not register Gens view plug-ins in the player's view container.
5. Remove Gens branches from chat processing and PvP relationship resolution; verify retained code does not perform a plug-in lookup per combat hit.
6. Assert at startup that no active plug-in type namespace contains `.Gens`.

Suggested fail-fast test:

```csharp
var forbidden = plugInManager.GetActivePlugInTypes()
    .Where(t => t.Namespace?.Contains(".Gens", StringComparison.Ordinal) == true)
    .ToArray();
Assert.That(forbidden, Is.Empty);
```

Adapt the enumeration call to the actual plug-in-manager API; the invariant is more important than the sample method name.

### 2.3 Master level and master skill tree

Relevant initialization and actions:

- `VersionSeasonSix/SkillsInitializer.cs`: `InitializeMasterSkillData()` and `MasterSkillPassivePowerUps.AddMissing(...)`;
- `Persistence/Initialization/MasterSkillTree.xml`;
- `GameLogic/MasterSkillExtensions.cs`;
- `PlayerActions/Character/AddMasterPointAction.cs`;
- `GameServer/MessageHandler/Character/CharacterAddMasterPointPacketHandlerPlugIn.cs`;
- master-level view plug-ins and chat commands.

Required removal:

1. Do not call `InitializeMasterSkillData()` or `MasterSkillPassivePowerUps.AddMissing(...)`.
2. Set `MaximumMasterLevel = 0`, do not build master-only character classes, and remove master relationships from the attribute graph.
3. Deactivate `CharacterAddMasterPointPacketHandlerPlugIn`, master-skill view plug-ins and master-level chat commands. A guard inside `AddMasterPointAction` is defense in depth, not the primary disable mechanism.
4. Delete existing master `SkillEntry` rows and normalize `MasterLevel`, `MasterExperience`, and `MasterLevelUpPoints` to zero before removing definitions.
5. Assert `GameConfiguration.MasterSkillRoots.Count == 0` and no configured skill has a non-null `MasterDefinition`.
6. With `MaximumMasterLevel == 0`, avoid building an unnecessary master EXP table in `GameContext`; use `Array.Empty<long>()` behind a tested conditional rather than evaluating the formula.

No separate master-skill thread exists in this codebase. The savings are configuration graph size, attribute subscriptions, login graph size, handlers and packets.

### 2.4 Socket system

Relevant sources:

- `VersionSeasonSix/Items/SocketSystem.cs`;
- `PlayerActions/Craftings/MountSeedSphereCrafting.cs`;
- `PlayerActions/Craftings/RemoveSeedSphereCrafting.cs`;
- socket option handling in `ItemPowerUpFactory`, serializers and item definitions.

Required removal:

1. Do not initialize socket item definitions, option definitions, seed/sphere items or NPC craftings.
2. Remove socket craftings from all NPC `ItemCraftings` collections.
3. Migrate or reject every persisted item containing socket option links before definitions disappear.
4. Deactivate socket-specific packet/view handlers if present for the selected client version.
5. Keep the S6 item wire serializer structurally compatible unless both server and client serializer are changed together. An empty socket field costs bytes on the wire but no recurring CPU loop; deleting it on only one side corrupts subsequent fields.

Socket support has no autonomous loop. Do not introduce a runtime `if (!Enabled) return` on every item calculation; eliminate the socket definitions so `ItemPowerUpFactory` has nothing to enumerate.

### 2.5 Doppelganger and Imperial Guardian

Relevant sources:

- `GameLogic/MiniGames/Doppelganger/`
- `GameLogic/MiniGames/ImperialGuardian/`
- `VersionSeasonSix/Events/DoppelgangerInitializer.cs`
- `VersionSeasonSix/Events/ImperialGuardianInitializer.cs`
- corresponding maps, gates, NPCs, tickets and message handlers.

`MiniGameManager.GetOrCreateAsync()` creates the context and then calls `EnsureGameLoopRunning()`. Therefore zero-loop removal requires making creation impossible:

1. Do not run the two event initializers.
2. Remove their `MiniGameDefinition` objects from `GameConfiguration`.
3. Remove Doppelganger and Imperial maps from `GameMapsInitializer` and their gates from `Gates.cs`.
4. Remove ticket items, NPC windows and enter/opening-state handlers.
5. Remove `Doppelganger` and `ImperialGuardian` cases from the Aegis build of `MiniGameManager`, or make the manager accept only an explicit Aegis allow-list (`BloodCastle`, `ChaosCastle`, retained Devil Square, Kanturu if required).
6. Add a startup assertion that no disabled mini-game definition exists.
7. Add a runtime metric assertion that `GetRunningMiniGames(disabledType)` is always empty and `MiniGameCount` returns to baseline after retained events.

Do not instantiate a context and cancel it immediately. Construction allocates a map, collections, cancellation sources and event handlers.

### 2.6 Compile-time trimming versus runtime deactivation

Runtime deactivation prevents work but the assemblies still contain IL metadata. If “zero RAM” means the disabled types must never load, create an Aegis-specific project/profile which excludes the directories from compilation:

```xml
<ItemGroup Condition="'$(AegisS2Profile)' == 'true'">
  <Compile Remove="Gens\**\*.cs" />
  <Compile Remove="MiniGames\Doppelganger\**\*.cs" />
  <Compile Remove="MiniGames\ImperialGuardian\**\*.cs" />
</ItemGroup>
```

This requires first removing all references and generated resources. Treat it as phase two after behavioral tests pass. It reduces assembly size and guarantees no reflection-based plug-in discovery, but it does not materially reduce CPU if the plug-ins were already inactive.

## 3. Allocation-free unexpected-packet rejection

### 3.1 Existing behavior

`PacketHandlerPlugInContainer<T>.HandlePacketAsync` already returns silently when `handler is null`; it does not log an unknown packet. However, `RemotePlayer.PacketReceivedAsync` has already:

- opened a logging scope;
- copied the packet into a 255-byte per-player buffer or rented from `MemoryPool<byte>.Shared`;
- run the admission guard;
- entered `RunPersistenceExclusiveAsync` before main dispatch.

Debug and exception paths call `buffer.ToArray().AsString()`, which allocates. Those calls must never occur for an expected reject flood.

### 3.2 Fast allow-list placement

Place a fixed packet allow-list after framing/decryption has exposed the real main/sub opcode, but before the persistence-exclusive lock and handler lookup. A 256-entry bitmap is constant-time and allocation-free:

```csharp
private static readonly bool[] AllowedMainOpcodes = BuildAllowedMainOpcodes();

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static bool IsAllowed(ReadOnlySpan<byte> packet)
{
    if (packet.Length < 3)
    {
        return false;
    }

    int opcodeOffset = (packet[0] & 1) == 1 ? 2 : 3;
    return (uint)opcodeOffset < (uint)packet.Length
        && AllowedMainOpcodes[packet[opcodeOffset]];
}
```

Grouped packets need a second 256-entry table keyed by main opcode and sub-opcode. Validate the minimum length before reading. For encrypted C3/C4 packets, admission must be placed after the network layer has verified/decrypted the packet; never classify ciphertext bytes as opcodes.

Fast rejection policy:

- increment an `Interlocked` counter or `System.Diagnostics.Metrics.Counter<long>`;
- no string interpolation, hex dump, exception, LINQ, `ToArray`, async state machine or database lock;
- use a token bucket per connection/IP; log only the first event and a periodic aggregate summary;
- disconnect after a configurable sustained threshold, not on one unknown packet;
- malformed length/header is a protocol violation and should close the connection without attempting packet parsing.

Return `ValueTask.CompletedTask` from a non-`async` fast path. Do not register dummy “do nothing” plug-ins: they add handler objects and dispatch work.

### 3.3 Required abuse tests

- 1, 10, 100 and 10,000 rejected packets/second per connection;
- random lengths 0-65,535, including truncated C1/C2/C3/C4 headers;
- all 256 main opcodes and all grouped sub-opcodes;
- encrypted packet with invalid checksum/serial;
- valid opcode in invalid player state;
- 1,000 concurrent slow senders and reconnect churn.

Pass: zero server exceptions, bounded connection memory, no log amplification, no persistence queries, no handler invocation and predictable disconnect behavior.

## 4. Inventory, warehouse and packet-layout harmony

### 4.1 Do not blindly truncate S6 packets

The Aegis client still speaks the S6E3 protocol. A fake downgrade must retain the S6 packet structure unless the C++ client and C# packet definitions are changed atomically. Sending a stock 1.02 packet length to an S6 parser can make the client interpret the next packet as item bytes.

OpenMU constants are in `src/DataModel/InventoryConstants.cs`:

- 12 equipment slots;
- 8 x 8 = 64 base inventory slots;
- up to four 4-row extensions;
- S6 personal-shop storage begins after the reserved extension range;
- warehouse is 15 x 8 = 120 slots.

For an S2 content profile on the existing S6 wire protocol:

1. Force every character's `InventoryExtensions` to `0` at creation and migration.
2. Disable `InventoryExtensionDeliveryPlugIn` and `VaultExtensionDeliveryPlugIn`.
3. Reject move requests targeting extension slots.
4. Before login release, quarantine any item whose slot is outside equipment + 64 base inventory slots and not in the S6 personal-shop reserved range.
5. In `UpdateInventoryListPlugIn`, filter by an explicit allowed-slot predicate before `.ToList()`; never send a hidden extension item.
6. Keep `FirstStoreItemSlotIndex` unchanged while using an unmodified S6 client. Changing it only on the server makes personal-shop items overlap extension slots.

Example policy:

```csharp
private static bool IsAegisVisibleInventorySlot(byte slot)
    => slot < InventoryConstants.FirstExtensionItemSlotIndex;
```

If personal shop items are concatenated, classify them separately instead of applying the inventory predicate to the combined collection. Validate each slot before serializing and cap `ItemCount` to the packet field width.

### 4.2 If converting the client to a true classic layout

Only then change `FirstStoreItemSlotIndex`, C++ inventory arrays, drag/drop bounds, shop offsets, item-move request validation, packet serializers and persistence migration in one versioned protocol change. Introduce a new `ClientVersion` rather than silently reusing the S6 identity. Add compile-time `static_assert` checks in C++ and packet-size tests in C#.

### 4.3 Ghost-item and overflow tests

- item in every legal equipment/base inventory/vault corner slot;
- multi-cell items touching right and bottom edges;
- item starting at last legal slot;
- forged item in first extension slot;
- duplicate slot, overlapping item, undefined item and invalid width/height;
- full inventory followed by pickup, trade, vault, chaos mix, relog and reconnect;
- personal shop open/close with zero inventory extensions;
- maximum serialized item option payload.

After every operation compare server occupancy grid, serialized `(slot, item-id)` pairs and client grid. A ghost item is a release blocker even if relog clears it.

## 5. BMD/server mismatch validation

### 5.1 Repository-specific reality

The current client has data-driven item JSON under `src/bin/Data/Items/` and legacy binary loaders for other data. Do not assume a stock `Item.bmd` is the only item source. The relevant runtime source must be identified from the exact client commit.

The client loads, among others:

- `Data/Local/<lang>/movereq_<lang>.bmd`;
- `Data/Gate.bmd`;
- `Data/Local/<lang>/Skill_<lang>.bmd`;
- item data through the current item data handler/fallback;
- model, texture and tooltip/localization data referenced by numeric IDs.

### 5.2 Canonical manifest

Generate a server export, not a hand-maintained spreadsheet:

```json
{
  "items": [{ "group": 0, "number": 0, "width": 1, "height": 3, "slot": 0 }],
  "skills": [{ "number": 1, "mana": 10, "type": "..." }],
  "gates": [{ "number": 1, "map": 0, "x1": 120, "y1": 120, "target": 2 }]
}
```

Write client-side extractors for BMD/JSON using the same structs used by the client loader. Normalize both exports, sort by numeric key, and compare in CI.

Required invariants:

- item `(group, number)` unique on both sides;
- identical dimensions, equip slot and class eligibility;
- every server item has client name/model/tooltip data;
- no client-exposed item is forbidden by the Aegis allow-list;
- skill ID, type, target mode, icon and required level agree;
- every gate source/target map exists, rectangle is within terrain bounds and destination is walkable;
- move-command gate IDs exist and no disabled map is reachable;
- fixed-index formats retain holes; IDs are never compacted after deletion.

### 5.3 Crash-oriented client test

Create an automated client session which renders every allowed item on ground, inventory, equipment, shop, trade and vault; opens every tooltip; casts every skill; and traverses every gate in both directions. Run under AddressSanitizer where supported and Windows Application Verifier/PageHeap for native bounds errors. Fail on missing texture/model, out-of-range index, malformed packet, access violation or log warning.

Hash the shipped data pack and store the manifest hash in both server build metadata and launcher manifest. The launcher must reject a mixed server/client data version before starting the game.

## 6. Persistence and query efficiency

### 6.1 OpenMU's actual provider

This repository uses EF Core with Npgsql/PostgreSQL. `AccountRepository` authenticates with a small projection, then `AccountJsonObjectLoader` and `JsonQueryBuilder` load the account aggregate as PostgreSQL JSON. Advice about SQL Server `Include()` or Dapper is not directly applicable until the provider is ported.

The character's `LearnedSkills` is one collection; master and normal skills are not separate login tables. Therefore excluding the whole collection would break classic skills. The correct Aegis optimization is:

1. remove master definitions from configuration;
2. delete master skill entries during migration;
3. retain only normal `SkillEntry` rows;
4. ensure the account JSON graph contains no forbidden references;
5. benchmark JSON payload bytes and deserialization allocations before/after cleanup.

`InventoryExtensions` is a scalar and costs essentially nothing to load; normalize it to zero rather than creating a special query. Vault storage is an account aggregate and `ShowVaultPlugIn` expects it. Deferring vault loading is an architectural change requiring an explicit repository method, lifetime tracking and save semantics; do not simply remove the navigation from `JsonQueryBuilder`.

If lazy vault loading is implemented:

- login returns account/characters/base inventory only;
- first vault open loads vault with a compiled, no-tracking read followed by explicit attach for mutation;
- only one load may run per player;
- closing/disconnecting must await save/dispose;
- trade and save paths must not interpret “not loaded” as “empty”.

### 6.2 Index and query audit

Use `EXPLAIN (ANALYZE, BUFFERS)` on the generated account JSON query. Verify indexes for:

- unique account login name;
- unique character name;
- character-to-account foreign key;
- item-to-storage foreign key and item slot if used in ordering;
- skill-entry-to-character foreign key;
- all foreign keys used in JSON subqueries.

Capture p50/p95/p99 authentication, full account load, save and vault-open latency. Track returned JSON bytes, logical reads/buffer hits and rows visited. An index is accepted only when the plan and workload demonstrate benefit.

### 6.3 SQL Server guidance for a deliberate provider port

If Aegis-Mu is ported to SQL Server, use `Microsoft.EntityFrameworkCore.SqlServer` and generated SQL Server migrations; do not point Npgsql migrations at SQL Server. Recommended starting connection string, subject to load test:

```text
Server=tcp:<host>,1433;Database=AegisMu;
Integrated Security=true;Encrypt=true;TrustServerCertificate=false;
Pooling=true;Min Pool Size=10;Max Pool Size=200;
Connect Timeout=5;Load Balance Timeout=300;
MultipleActiveResultSets=false;Application Name=Aegis-Mu-GameServer;
```

Sizing rule: `Max Pool Size` must be lower than the database worker/connection budget after reserving capacity for admin, migrations and other services. Start near peak concurrent database operations, not peak connected players. Measure pool wait time; raising the pool can make SQL contention worse.

EF recommendations:

- use `AddDbContextPool` only after proving no tenant/player state leaks through context instances;
- use compiled queries for authentication and hot lookup paths;
- project only required columns for authentication;
- use `AsNoTracking()` for read-only queries;
- avoid lazy-loading proxies and N+1 navigation access;
- use `AsSplitQuery()` only where a measured cartesian explosion exceeds the extra round trips;
- use bounded retry for transient errors, never retry non-idempotent saves blindly;
- keep a short command timeout for login and separate longer maintenance timeout;
- batch writes at explicit save boundaries, not per inventory movement unless durability demands it.

Do not use `NOLOCK`; dirty reads can duplicate logins, items or currency. Prefer `READ_COMMITTED_SNAPSHOT ON` after verifying application semantics. Enable Query Store, parameterized queries and actual execution-plan review. Dapper is useful only for isolated read projections; mixing Dapper writes with EF tracking in the same unit of work risks lost updates.

## 7. Disabled-class null/index audit

### 7.1 Static audit

Search for numeric class assumptions and direct indexing:

```powershell
rg -n "CharacterClassNumber|classCode|Class.*\[|\[.*Class|switch.*Class" `
  src/GameLogic src/GameServer src/Persistence/Initialization
```

Review especially:

- `CharacterClassHelper.NumberMapping`;
- `GameConfigurationInitializerBase` class switches;
- bot progression class arrays;
- appearance serializers and client character-model arrays;
- quest/evolution plug-ins;
- item class requirements;
- buff formulas and skill qualification;
- party composition/experience code.

Do not shrink wire-code-indexed arrays merely because Summoner/RF are disabled. Sparse class codes require either the original maximum-sized table or a dictionary keyed by enum. The safest pattern is `TryGetValue` plus a controlled reject, not `array[(int)classCode]`.

### 7.2 Party experience fact check

OpenMU party EXP is calculated in `src/GameLogic/Party.cs`. Audit it for properties (`CharacterClass`, level, map distance) rather than assumed contiguous class indices. Test every retained class combination and inject disabled/unknown class codes through persistence fixtures to ensure a controlled validation failure, not `IndexOutOfRangeException` or `NullReferenceException`.

### 7.3 Automated tests

Parameterized class-domain test:

```csharp
foreach (byte code in Enumerable.Range(0, 256).Select(i => (byte)i))
{
    // Decode/validate must either return an allowed class or a typed failure.
    // It must never throw IndexOutOfRangeException or NullReferenceException.
}
```

Run this domain against class creation, appearance serialization, party EXP, stat initialization, buff calculation, quest lookup and item eligibility. Add property-based tests for arbitrary level/stat/item combinations. Treat broad `catch (Exception)` as a test failure; it masks the defect.

## 8. Blood Castle and Chaos Castle stress protocol

### 8.1 Scenarios

Run at least:

1. idle server, no clients;
2. 500 authenticated clients at character selection;
3. 500 clients distributed across ordinary maps;
4. Blood Castle at capacity with maximum configured waves;
5. Chaos Castle at capacity with simultaneous AoE attacks and terrain changes;
6. combined event start/end/restart for 100 consecutive cycles;
7. reconnect storm during event transition;
8. forged disabled-feature packet flood concurrent with the event.

Use deterministic bot inputs and fixed random seeds where possible. Capture a trace from 60 seconds before event start through 60 seconds after disposal.

### 8.2 Metrics and thresholds

Track:

- tick/event-loop duration p50/p95/p99/max;
- time waiting for map/player/party/mini-game locks;
- monitor contention count and duration;
- thread-pool queue length and starvation events;
- allocations per combat action, packet and spawned monster;
- Gen 0/1/2 frequency, pause p99, LOH allocations and pinned objects;
- active `Task`, timer and cancellation-source count by feature;
- packets/sec, bytes/sec, send-queue depth and pooled-buffer rents;
- database command count, pool wait, p95/p99 duration and save conflicts;
- map object count, AI decisions/sec and pathfinder pool misses;
- event-state broadcast fan-out and serialized bytes;
- exception and warning counts by template, not rendered message;
- process CPU, working set and handles after each event cycle.

Initial release targets must be derived from the deployment SLA. At minimum enforce no monotonic growth across 100 cycles, no thread-pool starvation, no Gen 2 collection storm, no connection-pool exhaustion and event-loop p99 below the shortest gameplay timing deadline.

### 8.3 Allocation hotspots to inspect

- `.ToList()` in hot packet paths, including inventory serialization;
- LINQ over all players/objects per tick;
- closure creation in `ForEachPlayerAsync` and observer broadcasts;
- `Task.Run` per event/action;
- log interpolation and packet hex conversion;
- repeated item serialization-size computation;
- temporary hash sets and dictionaries per packet;
- repeated path creation when a pooled pathfinder is available.

Optimize only after trace evidence. Replace hot LINQ with indexed loops, reuse bounded buffers/sets per player or rent from pools, and preserve ownership/disposal rules.

## 9. Strict test matrix

### Startup/configuration

- Fresh database initializes with no Gens, master, socket, Doppelganger or Imperial definitions.
- Upgrade migration leaves no forbidden character, skill entry, item option, map/gate or event reference.
- Startup assertions reject a contaminated database before opening network listeners.
- Active plug-in inventory contains no disabled namespace/type.

### Network security

- Every disabled opcode/subopcode is rejected before persistence lock/action dispatch.
- Truncated and oversized packets cannot index outside spans.
- Reject flood is allocation-bounded and rate-limited.
- Valid S2-profile packets remain byte-for-byte compatible with the S6-based Aegis client.

### Character/progression

- Summoner/RF and all disallowed evolution codes fail creation and selection.
- Level 400 receives zero normal/master EXP and remains unchanged after relog.
- Master point packet has no active handler.
- Every retained class completes stat, buff, party and appearance tests.

### Inventory/persistence

- `InventoryExtensions == 0` for every character.
- No item occupies extension slots; no forbidden socket/master data exists.
- Inventory/vault/trade/shop/chaos/reconnect round trips preserve exactly one copy of each item.
- Login query plan and payload contain no disabled-feature aggregate.

### Events and lifecycle

- Disabled mini-game creation is impossible even with forged tickets/packets.
- Retained event loops cancel and dispose at end/shutdown.
- `MiniGameCount`, map count, timers, tasks, handles and heap return to baseline.
- Server shutdown completes with zero orphan task and no socket callback into disposed players.

### Client/data

- Manifest comparison reports zero item/skill/gate mismatch.
- Automated render/traversal suite covers all retained IDs.
- Mixed or stale data-pack hash is rejected by launcher.
- Native sanitizer/Application Verifier run reports no invalid access.

## 10. Optimization order

1. Make the Aegis allow-list explicit and add startup assertions.
2. Remove disabled initializers and migrate contaminated persistence data.
3. Deactivate/remove feature and packet/view plug-ins.
4. Prove Doppelganger/Imperial contexts can never be instantiated.
5. Add pre-lock packet admission with bounded aggregate telemetry.
6. Normalize inventory extensions and validate all slot boundaries.
7. Build the canonical server/client data manifest and CI diff.
8. Measure PostgreSQL account JSON query; optimize only proven graph/index costs.
9. Run class-domain/property tests.
10. Run event stress/soak tests, fix lifecycle leaks, then establish regression budgets.
11. Only after behavior is stable, exclude dead source files from the Aegis build for smaller assemblies and reflection surface.

The final acceptance artifact must contain commit SHAs, configuration/data manifest hashes, migration report, active plug-in inventory, packet allow-list, query plans, counter exports, traces and the complete test matrix result. “Feature hidden” is not evidence of zero overhead; absence must be demonstrated at configuration, registration, runtime-object and trace levels.
