# Confined program boundary

## Current implementation

This subtree ports the host-facing parts of `packages/codemode/src/tool-runtime.ts`,
`tool-schema.ts`, `interpreter/execute.ts`, and `packages/core/src/codemode`.
It now includes a real **JintCodeModeEvaluator**, backed by pinned managed Jint
and Acornima packages. No source is executed by default: the host must bind the
evaluator to a captured snapshot. This is a restricted implementation, not full
parity with the original interpreter's entire language/standard library.

- `ToolSnapshot.CodeModeDiscovery` captures exact dotted tool paths, descriptions,
  schema-derived TypeScript signatures, pinned listings, and namespace summaries.
- `CodeModeCatalog.Search` implements exact-path lookup (including bracket forms),
  namespace filtering, ASCII/camel-case tokenization, plural variants, source scoring,
  stable ordering, default page size 10, offsets, and remaining/next results.
- `RenderInstructions` applies the source's pinned-listing and round-robin budget
  policy. Instruction updates can use a complete replacement; compact update diffs
  are not implemented here. A changed hidden catalog still needs a new observation.
- `ToolSnapshot.WithCodeMode(evaluator, limits)` creates another captured snapshot.
  It does not mutate the registry, publish instructions, change permissions, or
  change earlier snapshots. No process-global evaluator registration exists.
- Only a snapshot explicitly bound to an evaluator advertises `execute` and has
  `CodeModeExecutable == true`. Disabling the execute permission still suppresses it.
- The program bridge limits source/data bytes, tool admissions (including search),
  retained logs/results, and JSON depth. It rejects blocked data-property names;
  those same names remain valid inert tool-path segments.
- Calls use the original captured `ToolInfo` and the existing codec/hooks/leaf
  authorization boundary. Calls remain owned until settlement. Program completion
  closes admission, cancels pending calls, and waits for leaf cleanup.
- The guest receives declared machine output, including explicit JSON null, or
  text/null when no machine output is declared. Display content is not substituted
  for declared output. Inline files stay host-side, retain admission order, and
  become outer tool content. Final output/metadata contain nested call statuses.
- Only declared tool/program diagnostics are recoverable guest errors. Host
  control failures are retained and rethrown after settlement even if an adapter
  incorrectly attempts to convert them to a normal program result.

`ICodeModeEvaluator` is a **trusted implementation contract**, not proof that an
arbitrary implementation is safe. A host must not pass unrestricted eval, a shell,
a C# scripting engine, or a `Task.Run`/`WaitAsync` timeout wrapper to this hook.

## Managed evaluator

The native QuickJS proposal is withdrawn. Do not add a native engine or another
process. The verified managed choice is **Jint 4.16.1 + Acornima 1.7.0**, not an
independent Esprima package. Jint's published NuGet metadata identifies commit
`474996a9e9c8f7a40ea429d56bd6bbe4c249ad06`; Acornima's metadata identifies commit
`401bd62d8aeb9f7cbe7b6147937a87e0c71747a8`. Their licenses are BSD-2-Clause and
BSD-3-Clause respectively. Jint ships a net10.0 asset and Acornima a net8.0 asset,
compatible with this net11.0 project. Jint main is v5 development: do not use its
new hardening/parser APIs as if they existed in the published v4 package.

Implemented boundary:

1. Parse with Acornima's `AllowReturnOutsideFunction` and
   `AllowAwaitOutsideFunction`. Validate node kinds and node-specific restrictions
   against the source interpreter. Use `OnToken`/`OnNode` to account for parsing
   work, in addition to an input-byte cap. Transform the last expression and
   implicit async body structurally, not through regex rewriting.
2. Use a fresh Jint Engine per invocation. Leave CLR namespaces, reflection,
   module loading and debugger disabled; set `Host.StringCompilationAllowed`
   false and `AgentCanSuspend` false. Install only captured tools/search and
   approved built-ins. No CLR objects, arrays, exceptions, `ToolContext`, registry,
   or service provider may cross through automatic object wrapping.
3. A syntax allowlist is necessary but insufficient: computed member keys,
   destructuring, aliases and built-in callbacks also require runtime enforcement.
   Block prototype traversal on data but retain inert tool path segments named
   `constructor`, `prototype`, or `__proto__`. If a construction is not enforced,
   reject it explicitly rather than claiming source parity. Jint's broader native
   JavaScript semantics do not automatically match the source's opaque references.
4. Bind explicit `ClrFunction` callbacks returning only guest values. Use an
   invocation-owned completion queue and manual guest promises: asynchronous tools
   run outside the engine, and only the owner touches guest objects or resolves
   promises. Avoid automatic TaskInterop, which converts canceled/faulted tasks to
   guest rejections and can wrap their CLR exception values. Permission decline
   must instead terminate the invocation through out-of-band control flow.
5. For v4 allocation accounting, keep engine work on one owner thread and maintain
   a cumulative program-scoped constraint across all pumps. Built-in LimitMemory
   resets per engine entry and explicitly skips checks after a thread change; it
   is not adequate as a whole-program async budget by itself. A thread is not an
   OS security boundary and is not a separate worker process.
6. Register cancellation, execution-check, time, recursion and stack-guard constraints;
   bound arrays and regex work. Use an operation deadline across parsing, pumps and
   conversion. `EvaluateAsync(..., cancellationToken)` alone only controls promise
   waiting. It does not preempt a busy synchronous loop. No budget can interrupt an
   arbitrary blocking CLR call, so the bridge must not expose one.
7. Observe `Advanced.PromiseRejectionTracker`, adopt the top-level returned promise,
   and stop pending guest work once that result has settled. Do not wait for all
   manual promises before declaring the program finished. Preserve the top-level
   result if interrupting leftover jobs; cancel and join owned tools separately.
   Exposed promise creation boundaries assign diagnostic ordinals; observation
   permanently removes failures, and warning text is captured at rejection time.
   Final warnings sort by creation ordinal. Reactions still use Jint's queue;
   exact source scheduling parity has not been runtime-verified.
8. Copy guest results via bounded, descriptor-aware traversal, not arbitrary
   `ToObject()` or JSON serialization that can invoke getters or `toJSON`. Apply
   undefined/non-finite/plain-data rules and retain files host-side. Live Map/Set
   values stay within the invocation and normalize to `{}` at JSON/tool boundaries,
   without traversing or exporting their entries. RegExp and URLSearchParams also
   normalize to `{}`; Date becomes ISO text/null, and URL becomes its href.

The limits now use `MaxAllocatedBytes`, `MaxRecursionDepth`,
`MaxExecutionChecks`, and `MaxSyntaxNodes`. They do not claim hard heap or stack
quotas. Allocation accounting spans the owner thread's entire invocation instead
of resetting on every pump. Execution checks include built-in checkpoints, not
only JavaScript statements. Input, result and log budgets remain separate.

### Supported scope and explicit limitations

- Actual JavaScript top-level await/return, final expression, closures, ordinary
  and async functions, conditionals, loops, switch, try/catch, identifier assignment,
  array binding patterns, and static-key object literals are accepted.
- Captured tools use exact namespace paths, including bracket notation. Tools
  are not CLR wrappers. Namespace enumeration uses inert enumerable child names;
  a tool named `then` must not become an implicit promise-assimilation call.
- Only the explicit static/member allowlists in `JintCodeModeRealm` are exposed.
  This includes the source's six Promise combinators/resolution functions, promise
  chaining, common nonmutating array transformations, string/number operations,
  selected Math methods, and Object keys/values/entries/hasOwn/is. Other members
  are unavailable; no fallback to arbitrary prototype traversal exists.
- let/const object and array binding patterns lower to ordered native lexical
  declarators, not initialized placeholder variables followed by assignments.
  This preserves TDZ across all bound names and initializes each name only after
  its value/default is ready. Computed keys, repeated property keys, nested object/
  array patterns, aliases, elisions and rest use guarded pattern cursors/objects.
  RHS expressions run once; property reads and defaults retain source order and
  leaf identity. Await/yield stays in its actual initializer branch. Classic for
  initializers use the same declaration form and retain native per-iteration let
  cells; direct array length/method writes are not enabled by binding support.
- Functions with binding-pattern parameters capture raw arguments and initialize
  all parameters through one native let declaration before entering a separate
  original-body block. Later parameters remain in TDZ during earlier defaults,
  and closures retain the parameter environment rather than body-local bindings.
  Rest arguments are copied through a bounded guest-data helper. Generator
  parameter initialization remains deferred to the first next request. Ordinary
  body var declarations now use cells in the body scope, so they do not hoist into
  parameter defaults or conflict with parameter bindings. A body var can shadow a
  parameter after declaration while earlier reads still see that parameter.
- Catch binding patterns receive source-shaped name/message/errors data for native
  errors, never a host stack/prototype wrapper; user-thrown data keeps its identity.
  Catch and var bindings now declare into live per-scope cells, without reserving
  user names like let/const. Self/forward defaults and keys fall through to the
  outer scope until the relevant declaration executes. Closures made during an
  earlier default retain the same environment and can observe a later declaration.
  Repeating a declaration fails after its initializer is evaluated; it does not
  silently overwrite a native hoisted var. Duplicate var-pattern names use that
   same execution-time rule instead of becoming duplicate generated let bindings.
- Error constructors now create branded plain guest data with enumerable name and
  message fields; AggregateError additionally consumes a guarded iterable into
  errors. Constructor options, including cause, are ignored as in the source;
  explicitly assigned data fields remain exportable. Brands stay private and
  support source error instanceof checks and coercion without exposing prototypes.
  User throws and explicit promise rejections carry a private envelope, preserving
  payload identity when caught and distinguishing uncaught data from runtime
  diagnostics. Native errors are adapted at catch/rejection boundaries; no CLR
  exception or stack is projected into guest data. Diagnostic formatting uses
  bounded own-data traversal, not guest getters, toJSON, or queue-reentrant calls.
- Object spreads use an own-data view: null/undefined and builtin value handles are no-ops; arrays,
  primitives and runtime references are rejected, matching the source's object
  spread boundary. Array and call-argument spreads accept arrays, strings, Maps, Sets and URLSearchParams,
  preserving holes-as-undefined and Unicode iteration, including guarded custom
  synchronous iterables. No prototype fallback is introduced.
- Computed object-literal fields/methods are structurally lowered through a key
  validator before their value expression. Only string/number and the two supported
  iterator-symbol keys are accepted; blocked prototype keys fail before the value
  runs. Accessor properties are replaced by an execution-site UnsupportedSyntax
  guard without emitting their body/key subtree, matching the source's refusal
  before key/value evaluation. Other unsupported forms include
  classes, this/super, the `in` operator, imports and dynamic source
  compilation are unavailable. Supported `new` forms dispatch by their source
  identifier name to captured builtin constructors, as upstream does; a local
  binding with the same name does not replace that constructor. Ordinary calls
  still follow lexical bindings. Unsupported constructor names/callee shapes are
  replaced with a runtime UnsupportedSyntax guard before their subtree reaches
  Jint. The guard does not evaluate callee/arguments and a skipped branch does not
  fail, matching upstream evaluateNewExpression rather than native arbitrary new.
- Map/Set constructors accept no input/null/undefined or the supported synchronous
  iterables (arrays, strings, Maps, Sets, URLSearchParams, and custom synchronous
  iterator records). Map entries must be data pair objects;
  tools/namespaces/functions cannot masquerade as entry objects. Native managed
  collections retain insertion order, SameValueZero key equality and object identity.
- Map exposes only `size`, `get`, `set`, `has`, `delete`, `clear`, `forEach`,
  `keys`, `values`, `entries`, plus static `Map.groupBy`. Set exposes only `size`,
  `add`, `has`, `delete`, `clear`, `forEach`, `keys`, `values`, `entries`, and the
  seven source set operations (`union`, `intersection`, `difference`,
  `symmetricDifference`, `isSubsetOf`, `isSupersetOf`, `isDisjointFrom`).
  Operations also accept plain Set-like records with a coercible non-NaN size,
  supported `has`/`keys` callbacks, and an array returned by `keys`, matching the
  source's record contract. Size is truncated/clamped as in the source; callbacks
  run under the invocation budget. Difference iterates the copied result when
  required, so callback mutations of the original target do not change that branch.
- Collection enumeration methods return materialized arrays, not leaked native
  iterator objects. `forEach` snapshots entries before invoking callbacks, matching
  the source rather than JavaScript's live native forEach behavior. Callback arguments
  are value/key/collection (Set uses value twice), no this-holder is provided,
  and returned promises are not automatically awaited. Grouping consumes the live
  supported iterable with a checked callback per item. Detached methods retain their
  receiver. Tool references and detached Promise methods are not accepted callbacks.
- Implicit collection iteration works in for-of, array binding, spreads and
  Array.from. for-in rejects collections, directing callers to for-of. Constructors,
  materialization, mutations and grouping buckets check
  the invocation budget and an item ceiling of min(MaxBoundaryBytes, 100000).
- Date now supports construction from time/string/copy/components, static now/parse/UTC,
  and the complete member allowlist in the source's `stdlib/date.ts`. Detached
  methods retain the date receiver. Date() and string coercion use ISO text rather
  than host-local display text; invalid values produce the source's Invalid Date or
  null boundary value. Setters capture the original time before argument coercion,
  then commit the computed timestamp. Parsing, time clipping, timezone and DST
  behavior delegate to managed Jint; full host/V8 conformance is not asserted by a build.
- RegExp literals and callable/new construction use a guarded Jint constructor.
  Flags are limited to d/g/i/m/s/u/v/y, without duplicates or simultaneous u/v.
  Only source-listed properties and test/exec/toString plus static escape are
  exposed. Assignment to lastIndex preserves its raw value; exec/test apply
  the source's length coercion and restore non-stateful lastIndex. String
  match/matchAll/search/replace/replaceAll/split are integrated; matchAll is
  materialized under the item/budget ceiling. Match results retain index/groups/
  indices, omit input, and filter blocked named-group keys. Replacement callbacks
  use the supported callback boundary, not CLR projection. Regex matching uses
  Jint's configured per-operation timeout (at most 250 ms) plus the invocation
  deadline; no raw unrestricted .NET-regex execution path is exposed. Native regex
  backend/conformance differences still require the prohibited runtime verification.
- URL uses the already-referenced managed `AngleSharp.Dom.Url` parser/API, not
  System.Uri as a guessed browser substitute and not a new native dependency.
  Callable URL/URLSearchParams require `new`; URL.parse/canParse return null/false
  on invalid addresses. Source-listed URL fields and toString/toJSON are exposed.
  Writable fields use guarded setters; origin/searchParams remain read-only.
  URLSearchParams supports query text, data records, supported pair iterables and
  cloning; append/delete/get/getAll/has/set/sort/forEach/keys/values/entries/toString
  and size are implemented. Lists preserve duplicates/order, use form encoding,
  and forEach snapshots. URL.searchParams keeps stable identity and synchronizes
  in both directions after URL/search-parameter mutations. Implicit iteration is
  supported without exposing host objects. AngleSharp parsing and
  invalid-field setter behavior need browser-conformance verification; no full
  WHATWG edge-case equivalence is claimed from package API compatibility alone.
- `instanceof` is structurally routed to nominal checks for Map, Set, Date,
  RegExp, URL, URLSearchParams and branded errors, without arbitrary prototype traversal. Other
  constructors remain unsupported.
- Ordinary data writes now capture the evaluated object/key and old value once,
  before evaluating the RHS. Plain own-data objects, canonical array indices,
  RegExp.lastIndex, and URL data fields are supported; runtime namespaces,
  functions, promises, collection internals, and other opaque handles cannot be
  mutated. Data keys constructor/prototype/__proto__ remain blocked. Arrays reject
  direct length writes, method writes, and noncanonical/non-index property writes,
  including existing match metadata. Index growth retains the configured item ceiling.
- Arithmetic/bitwise compound writes use the source's data-only operand checks,
  primitive conversion and 32-bit shift rules. A captured member reference remains
  stable across an awaited RHS. Identifier compound writes use the same conversion
  path. Logical &&=/||=/??= use an invocation-local guest reference slot and native
  short-circuiting, so skipped RHS expressions are not called or awaited. URL origin
  is captured as a read-only reference: write rejection happens after RHS evaluation,
  and a skipped logical write does not spuriously fail.
- Prefix/postfix ++/-- on identifiers and data members use source numeric coercion
  without invoking an ordinary object's valueOf/toString hooks. Postfix returns the
  previous numeric value, not the original string/object. Before an object/array
  field is mutated, an iterative check rejects circular insertion while treating
  closures and builtin-value internals as opaque. Existing receiver/leaf identity
  is retained; no JSON copy is substituted for the assigned value.
- Delete on data members is supported, including sparse array holes.
  Nonconfigurable array length and RegExp.lastIndex return false, matching source
  Reflect.deleteProperty behavior. URL fields and opaque references cannot be
  deleted. Optional member chains use a guarded receiver chain and native optional
  calls, retaining short-circuit state until deletion completes. Receivers and keys
  run once; a skipped receiver does not evaluate subsequent keys (including awaits).
  Parenthesized chain boundaries retain their ordinary failure behavior. Optional
  call chains now capture each callee before arguments, retain bound intrinsic
  receivers, and skip arguments and later keys on a nullish optional callee.
  Await and spreads remain in their original argument/key branches; no synthetic
  asynchronous wrapper is added around a skipped call.
- Object/array destructuring assignment uses ordered plans rather than native
  unguarded property assignment. The RHS is evaluated once and its original identity
  is returned. Keys, property reads, defaults, rest collection and setters execute
  in source order. A member LHS is resolved only after its value/default has been
  obtained; earlier writes remain committed when a later binding fails. Duplicate
  keys, nesting, aliases, elisions and rest targets are supported in assignment
  plans. Defaults execute only for undefined, never for null or an existing value.
  Array prefixes close an unfinished iterator; acquisition/next failures do not
  close it, and consumer errors retain precedence over cleanup errors.
  Await in the RHS and free await in assignment defaults, computed keys and member
  targets are supported. Await-bearing plans become comma/conditional expressions
  over compiler-reserved per-activation temporaries, not async callback thunks.
  A defined value skips its default without performing an await. The captured RHS,
  object/consumed-key state, target evaluation and cursor state survive suspension.
  Owner-local cursor frames distinguish next failure, exhaustion and consumer
  failure; generated catch/finally boundaries unwind them before surrounding user
  handlers/finalizers. Nested calls get independent frame owners. Cleanup errors
  do not replace an original consumer error, and cancellation cannot run user
  cleanup past its checkpoint.
- for-of/for-await assignment headers now accept member, object and array targets.
  A hidden per-iteration input is assigned before the original body, preserving
  input identity and target/default ordering. Inner assignment cursors unwind
  before native for-of closes its outer iterator. Loop declarations keep their
  per-iteration binding path using ordered declarators. A private outer lexical
  block reserves let/const loop names while the iterable/key source is evaluated.
  Its unreachable declarations are never initialized, so an RHS-created closure
  retains the source's TDZ even after the loop finishes. Each iteration gets fresh
  user binding cells, and source labels remain attached to the actual loop. var
  loop bindings are mutable and iteration-local, without a TDZ shadow over the RHS.
  for-in uses a captured key array, so later deletions do not erase queued keys.
  Direct member targets in for-in are unsupported upstream's evaluateForInStatement,
  so they are not a parity TODO.
- Bare await/yield in formal parameter initializers is rejected by the source
  JavaScript grammar (and the pinned Acornima parser has explicit diagnostics for
  both). It is not an unimplemented language extension. Valid defaults can create
  async functions/generators or return promises without implicitly awaiting them;
  no TypeScript/transpiler asset was added to expand the accepted grammar.
- Ordinary var follows the existing interpreter's ScopeStack, not native JavaScript
  hoisting. Program, block, catch, switch and loop environments own progressive
  cells. A declaration adds a name only after evaluating its input/defaults and
  rejects an existing native or cell binding in that same scope. Reads/writes
  search live cell environments from inner to outer, then use the nearest native
  lexical binding or a permitted global. Unknown reads/writes retain ReferenceError
  behavior; typeof an undeclared name returns undefined without suppressing a real
  native lexical TDZ error. Logical assignments and updates retain conditional
  evaluation and source numeric conversion on cell-backed names.
- Scope analysis distinguishes function parameters from the body block, loop RHS
  TDZ scopes from iteration scopes, and a switch discriminant from its case scope.
  Functions hoist only in the source's Program/Block positions. Declarations in
  other positions are source no-ops rather than accidental native hoisted bindings.
  Named function expressions do not gain a native private self-binding that the
  source createFunction does not install. let/const continue to use native lexical
  cells; the cell layer does not replace or eagerly initialize them.
- Classic var loops keep one loop environment; let loops copy the hidden environment
  binding with native per-iteration cells and discard body-added vars before update.
  Const headers keep immutable user values while fresh iteration environments hold
  body vars. Header-created closures retain the initial environment, while body
  closures retain their own iteration environment. No source environment, fallback
  thunk, or cell object is exposed through CLR wrapping or the tool/JSON boundary.
- Symbol is a confined namespace exposing only Symbol.iterator and
  Symbol.asyncIterator, not a symbol constructor/registry or other well-known
  symbols. Plain data objects can store these keys. Protocol functions are stored
  behind guarded guest factories; explicit symbol-member reads unwrap the original
  function identity. Rest/spread preserve the guarded stored factory. JSON/tool
  boundaries omit symbol properties, as source Object.entries-based copying does;
  a symbol used as a data value is still rejected.
- Custom iterator factories and step records must return data objects, not runtime
  references. next is captured once; return is read when closing. Calls use the
  source's no-this callback convention and checkpoints precede each step/cleanup.
  Sync next does not auto-await a promise; async next/return settle through owned
  Jint promises, then validate the record. for-of, for-await, array binding/spread,
  Array.from and Promise iterable consumers use guarded guest factories. Managed
  Map/Set construction, Map.groupBy and URLSearchParams pair consumption use an
  explicit cursor preserving the source's consumer-error/IteratorClose precedence.
  Cancellation checkpoints prevent user cleanup from extending an interrupted
  invocation. No unrestricted prototype or CLR object surface is exposed. Exact
  event-loop/conformance timing still needs runtime verification, which was not run.
- Arbitrary symbols, BigInt and TypeScript are not implemented. TypeScript is a
  real profile-specific parity gap: upstream interpreter/execute.ts runs the
  node/Bun transpile.node.ts frontend (TypeScript transpileModule) before Acorn.
  Its workerd profile deliberately uses transpile.workerd.ts, a plain-JavaScript
  pass-through. No additional language frontend or dependency was added here.
- JSON.parse accepts a string plus optional reviver: it validates
  the initial JSON before callbacks, visits children postorder, supports deletion
  (including array holes) and root replacement, and ignores noncallable revivers.
  JSON.stringify supports callable and array property-list replacers, preorder
  callbacks, omission/null handling, and numeric/string indentation capped at ten
  characters. Callbacks get key/value, not a this-holder, and async callback values
  are not implicitly awaited. Original input is validated before a replacer can
  filter it, preserving the source's blocked-key and opaque-reference boundary.
  Tool references and detached Promise methods remain invalid JSON callbacks.
  Property-list names are deduplicated in input order; blocked property-list names
  are explicitly rejected rather than traversing inherited prototype properties.
  Traversal and incremental formatting remain depth/byte/checkpoint bounded. Only
  primitive quoting/number formatting uses Jint's original serializer, so guest
  getters and toJSON methods are never invoked through a native serialization path.
  Console dir/table use bounded plain-data formatting, not full table rendering.
- Some unsupported syntax still fails preflight, including dead branches. Constructor,
  accessor, class, tagged-template, this, with, debugger and dynamic-import refusals
  now follow execution-site dispatch where replacing the entire
  rejected subtree preserves confinement. Statically spelled blocked object keys
  also route through the guarded property/read boundary rather than failing merely
  because an unused branch contains them. This does not claim all preflight/runtime
   differences are fixed. BigInt literals use an execution-site invalid-data guard,
   not native BigInt evaluation.
  Jint's normal JavaScript binding/built-in semantics are used within the subset;
  parser early-error timing, duplicate native lexical/function declarations, error
  normalization and exact promise observation timing are not claimed fully equivalent.
- Sync/async generator declarations, expressions and object methods now lower to
  ordinary capture functions returning an opaque GeneratorValue. The capture
  function does not evaluate the original parameters or body: a private thunk
  creates the Jint continuation only on the first next request. This follows
  source invokeFunction/createGenerator, including return/throw before first next
  without starting parameter defaults. Original argument values and closure
  references are retained; native let/const TDZ and progressive cell bindings remain separate.
- Generator handles expose only next/return/throw and the matching iterator symbol.
  Methods retain the handle receiver. A synchronous reentrant request fails with
  TypeError; completion, return values and thrown values follow source terminal
  behavior. Async handles serialize requests in FIFO order on the existing guest
  promise queue. A completed/before-start return awaits its value before the next
  queued request. No TaskInterop, CLR wrapper, per-generator thread, or raw native
  generator/prototype is exposed. Queue size and execution use invocation bounds.
- Direct yield and control resumption use the private continuation. yield* routes
  through a guarded delegation adapter that captures next, reads return/throw at
  request time, closes on a missing throw method, and retains consumer-error
  precedence. Private yield boxes preserve raw nested promise values supplied by
  async iterators while async-from-sync delegated values are awaited, matching
  source delegateYield rather than blindly accepting native async yield* coercion.
  Async terminal return values are adopted before the outward done record settles.
- Synchronous generator handles are accepted by synchronous iterable consumers;
  async generators are rejected there and use for-await/async delegation instead.
  Yield inside assignment defaults/keys/targets uses the same per-activation
  pattern cursor state as await, so return/throw can unwind unfinished inner
  cursors before user handlers/finalizers. Lazy defaults, yielding finally blocks,
  and request queues remain owned by the invocation. On top-level completion or
  interruption the realm abandons unused continuations and clears queues; it does
  not synthesize return(), execute generator cleanup code, or wait for un-awaited
  generators. Underlying host tool work is still cancelled/joined by CodeModeTool.
- These are source-derived lifecycle and boundary implementations, not a claim
  that all Jint/source scheduling details have been verified. Exact promise reaction
  ordering, full generator/async-generator conformance and the concrete boundaries
  below still require subsequent verification/porting. No
  generator, JavaScript, or regex program was executed to validate this change.
- Plain-data cycles, functions, promises, accessor-backed output, other non-plain objects and
  runtime references are rejected at the boundary. Data-only guest conversion
  never uses arbitrary ToObject or CLR object wrapping. Map/Set contents are not
  visited during normalization: even a self-containing collection becomes `{}`.
  A JSON function replacer sees the original live Map/Set/RegExp/URLSearchParams
  before its returned value is normalized; Date and URL enter the callback as
  their source-defined ISO/null and href values. Entries are not emitted implicitly.

### Remaining concrete execution-conformance boundaries

- Parser/native early errors can still precede the source interpreter's execution-
  time checks for some duplicate declarations and unsupported syntax. Only the
  explicitly replaced subtrees use execution-site guards; this is not a blanket
  acceptance of unimplemented syntax.
- Promise diagnostic ordering now uses explicit creation ordinals, permanent
  observation and a final warning snapshot. Async calls, tool calls, constructors,
  chaining, combinators and async-generator requests have tracking adapters.
  Native promise assimilation, reaction scheduling and combinator observation
  sequencing still need source-conformance work; sorting warnings does not establish
  equivalence with the source Effect scheduler.
- Branded error construction, throw envelopes and native-error adaptation are
  implemented, but exact native runtime-error messages, diagnostic locations,
  builtin/coercion edge cases and browser/V8 Date/RegExp/URL conformance still need
  targeted verification and any resulting implementation work.
- TypeScript erasure remains a separate Node/Bun-profile frontend decision; no
  additional language, dependency or native engine was added.

The original is also an in-process capability-confined interpreter: its limits
are cooperative timeout, admitted calls and retained output, not an OS memory
sandbox. Jint's documented threat model likewise explicitly disclaims OS isolation.
Hard containment of hostile multi-tenant code would require a separately approved
process/container architecture; it is outside this change and is not proposed here.

Verified references:
- https://api.nuget.org/v3-flatcontainer/jint/4.16.1/jint.nuspec
- https://api.nuget.org/v3-flatcontainer/acornima/1.7.0/acornima.nuspec
- https://github.com/sebastienros/jint/blob/v4.16.1/.github/THREAT_MODEL.md
- https://github.com/sebastienros/jint/blob/v4.16.1/Jint/Engine.Async.cs
- https://github.com/sebastienros/jint/blob/v4.16.1/Jint/Engine.Advanced.cs
- https://github.com/sebastienros/jint/blob/v4.16.1/Jint/Constraints/MemoryLimitConstraint.cs
- https://github.com/adams85/acornima/blob/401bd62d8aeb9f7cbe7b6147937a87e0c71747a8/src/Acornima/ParserOptions.cs

## Integration handoff

The real evaluator is available, but Session/Instructions configuration remains
owned by that implementer. Compile validation is not adversarial/runtime testing.

1. Capture the Location's normal permission-filtered snapshot, then bind the
   host-owned adapter with `snapshot.WithCodeMode(new JintCodeModeEvaluator(), limits)`
   and explicit finite `CodeModeLimits` selected by the host.
2. Replace the blanket nonempty-catalog rejection with a check for an unbound
   nonempty catalog (`!snapshot.CodeModeExecutable`). Never equate a catalog with
   execution support.
3. Feed `CodeModeDiscovery` into `core/codemode` instruction observation, preserving
   initial/current hashes and chronological updates. Do not use flattened direct
   provider names as JavaScript paths. Keep retained baseline renderers available.
4. Advertise and invoke `execute` from that same snapshot. The normal Session
   attempt publishes the outer durable tool result; the program bridge does not
   create a second durable model/tool loop or independent permission graph.
5. Rebind newly captured snapshots after MCP/registration reloads. Search within a
   running program remains bound to its original snapshot.

## Verification and remaining compatibility work

No tests, program execution, tool calls, providers, or database operations were
run for this change. Pinned dependencies were restored by the allowed .NET 11
build. Build artifacts are isolated under `C:\tmp\opencode\codemode-jint-build`.
Public package metadata and implementation source were read during assessment.
The full Core build after the error-normalization constructor/callback changes
passed with the pinned .NET 11 SDK: 0 warnings and 0 errors. This establishes C#
compilation only, not generated JavaScript validity or execution parity.

The default-MCP readiness blocker is **not resolved by this subtree alone**:
the Session owner still needs to bind snapshots and provide the corresponding
instruction observation/renderer. The evaluator is implemented, but adversarial
sandbox verification and complete source language/standard-library parity remain.

Schema signatures conservatively render unsupported/unresolved constructs as
`unknown`; the source's special Effect-number sentinels are not reproduced.
Search progress is present in final nested-call metadata; synchronous search does
not synchronously await the asynchronous live-progress subscriber. Managed allocation
accounting is not a hard heap bound on the host or external tools. Inline media
is host-owned and is not part of the source's result/log text budget.
