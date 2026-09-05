# Managed shell analysis

This is a bounded, non-executing scanner, not a full POSIX or PowerShell parser.
It adds assignment and control-flow classification to `ShellSyntaxScanner` without
loading a parser dependency, spawning a parsing process, or evaluating expressions.

## Source contract

The source references are `packages/core/src/shell/scan.ts` and
`packages/core/src/shell/parse.ts` in the TypeScript repository:

- The default source path walks Tree-sitter `command` descendants. The portable
  path scans structure and nested expansions, then maps raw command words to
  permission resources and prefix grants. This implementation does not claim to
  replace those grammars completely.
- Both source paths inspect command-bearing bodies without proving that a branch,
  loop, function or script block will execute. This scanner does the same for its
  supported structures. It does not propagate assignment values, resolve aliases,
  evaluate conditions, or infer the current directory from a sequence of commands.
- POSIX leading scalar assignments are excluded from command words, but remain in
  the enclosing raw permission resource. Assignment-only statements contribute
  their scanned substitutions, not a fabricated executable name.
- PowerShell scalar assignments distinguish expression-valued RHSs from command
  pipelines. Only actual command spans and nested command-bearing expressions
  become resources. No CLR or PowerShell expression evaluator is used.

## Added grammar

- POSIX: scalar `NAME=value` / `NAME+=value` words, including environment prefixes;
  `if`/`elif`/`else`, `while`/`until`, word-list `for`/`select`, brace/subshell groups,
  simple function definitions, and pipeline negation. Conditions and every body
  are scanned. Header values are not mistaken for command names.
- POSIX expansions: existing `$()` plus backticks with one source-defined escape
  layer removed, `<()`/`>()` process substitutions, simple `${NAME}` and scalar
  fallback operators `-`, `+`, `=`, `?` with optional `:`. Nested substitutions
  are scanned even when a fallback might not run.
- POSIX arrays: whole-array `name=(...)` / `name+=(...)`, element `name[key]=...`
  / `+=...`, and keyed elements inside constructors. Constructor elements are
  words, never command statements. Single-quoted values and array-body comments
  remain literal/non-executable; double-quoted/unquoted values scan their actual
  command, backtick, arithmetic and process substitutions. Assignment metadata is
  kept separately from raw text, so an `=` inside a subscript's substitution does
  not turn an assignment into a fabricated executable name. Assignment-only arrays
  contribute nested commands and any real redirect resource, as before.
- Common array/positional expansions include `[@]`, `[*]`, indexed subscripts,
  length (`#`) and key enumeration (`!`), slices, default operators, prefix/suffix
  removal and pattern replacement. `$@`/`$*` and their braced forms stay dynamic.
  The scanner does not enumerate elements, resolve array kinds, evaluate keys,
  calculate slice offsets, perform pattern matching or propagate assigned values.
- POSIX arithmetic: `$((...))`, legacy `$[...]`, standalone `((...))`, and
  three-clause arithmetic `for` headers. Nested parentheses, bracket subscripts,
  double-quoted portions, parameter fallbacks, command substitutions and backticks
  are scanned structurally. Named parameter subscripts also collect their nested
  substitutions. Arithmetic names/operators are not executable command names.
  Every syntactic command substitution receives its normal permission resource,
  including substitutions in clauses or branches that might not run.
  No arithmetic values, recursive variable contents, loop conditions or iteration
  counts are calculated. Arithmetic used as an executable or directory remains
  dynamic and is rejected by the existing guards.
- PowerShell: simple scalar and environment variables, assignments and compound
  assignments, quoted strings, decimal numeric operands, selected arithmetic and
  comparison/logical operators, and single-variable postfix increment/decrement.
  Parenthesized command pipelines and `$()` are scanned, not evaluated.
- PowerShell structures: `if`/`elseif`/`else`, `while`, `do`/`while` or `until`,
  three-clause `for`, `foreach (... in ...)`, simple `function`/`filter` bodies,
  `try`/`catch`/`finally`, and script blocks in command arguments. Function
  bodies are inspected even when no call is visible. Return/throw/exit pipelines
  are scanned; unlabelled break/continue are classified as control statements.
- PowerShell expressions now include literal-name instance/static members and
  method calls, bracket indices/slices, casts and type literals, qualified type
  names, one-dimensional array types and simple generic type arguments. Nested
  method/index values use the expression scanner; command pipelines must be
  parenthesized. No reflection, CLR construction, type lookup, property access,
  index calculation or method invocation occurs during scanning.
- PowerShell arrays include `@(...)`, expression-mode comma lists/unary commas and
  ranges. Hashtables accept literal identifier/string keys and scanned expression
  or command-valued entries. Simple named splats such as `@args` are accepted only
  as command arguments. Values are never propagated from assignments or hashtables
  to resolve an executable, argument list or directory. Use `@(...)` rather than
  unparenthesized comma lists in command-argument mode.
- Typed catches accept comma-separated literal type constraints. `param(...)` and
  function/filter parameter lists accept type constraints, attribute argument
  lists and expression defaults. Attribute preambles before `param`, including
  `CmdletBinding`, are structurally scanned. Attribute named arguments are labels,
  not executable names; their values and script blocks are scanned. No attribute
  constructor or parameter binder is executed. Attribute-bearing declarations
  retain raw permission resources even when no nested command is present.
- POSIX `case`: selector/pattern substitutions, optional opening pattern parentheses,
  alternatives, all arm bodies and `;;`, `;&`, `;;&` terminators. Pattern words are
  not executable names. PowerShell `switch`: parenthesized input, literal/default
  labels, scanned script-block predicates, all bodies and the full `-exact`,
  `-wildcard`, `-regex`, `-casesensitive` flags. Neither patterns nor regexes are
  evaluated by the scanner. File-input and abbreviated switch flags remain rejected.
- PowerShell stop parsing: a generic argument whose decoded spelling is `--%`
  starts a literal tail after an executable word. Ordinary backtick escapes and
  embedded quoting can spell the marker, matching the portable walker; a literal
  string starting with a quote does not activate it. Only double quotes toggle
  the tail's pipe boundary. CR/LF always ends the tail; an unquoted `|` ends it and
  normal command/pipeline scanning resumes. Semicolons, ampersands, braces,
  parentheses, redirects, comments, backticks and `$()` inside the tail are data,
  not command resources. The tail remains one raw argument in the source resource,
  rather than being retokenized into executable fragments. Environment expansion
  such as `%NAME%` is not evaluated by the scanner.

### Here-bodies and redirection

- POSIX `<<` and `<<-` use quote removal only on the delimiter. Any quoted or
  escaped delimiter portion makes the body literal. `<<-` strips leading tabs for
  delimiter matching. Expandable bodies join odd-backslash newline continuations;
  the resulting logical text is scanned for command substitutions, backticks and
  supported parameter fallbacks and arithmetic expansions. Ordinary single/double quote characters in the
  body are data and cannot hide a substitution. Literal bodies are never scanned
  as executable statements. ANSI/localized delimiter quoting remains rejected.
- Here-documents are queued at their header and consumed in order at the following
  newline, including multiple documents on one line. Permission resources retain
  the original source through their closing delimiter. Missing bodies/delimiters
  fail closed. A pending body cannot be bypassed by closing a surrounding structure.
  Command/process substitutions now own an independent pending-body context, as
  source `bashDelimited` does. Their internal newlines and here-documents cannot
  drain a pending outer header; returning restores the outer context, including on
  scanner failure. Decoded backticks have their own scanner context. Multiline
  quoted words/delimiters and parameter/arithmetic expansions are lexical units,
  not outer header newlines. A bare multiline group/control body crossed while an
  outer here-document is pending remains rejected: that cross-scope ownership is
  not classified. Literal body lines never become a fallback command list.
- POSIX `<<<` parses one ordinary expandable word, not a command list. PowerShell
  `@'`/`'@` bodies are literal; `@"`/`"@` bodies scan `$()` substitutions with
  PowerShell backtick escaping. Headers require a newline and closing markers
  start a physical line. Body words never become command resources by themselves.
- Redirect-only statements, assignment-only redirects, scalar-expression redirects
  and trailing compound redirects now produce a raw resource with no invented
  command words. Nested commands retain their own resources. Ordinary command
  resources retain their actual redirect text. POSIX file modes include `<>`,
  `>|`, `&>` and `&>>`, in addition to existing file and numeric-stream redirects.
  PowerShell stream merging uses only source-listed streams 2–6 or `*` into 1.

## Permission boundary

`LocalShellPolicy` still scans the entire source before requesting permissions or
returning a prepared invocation. Directory checks, external-directory approval,
shell-resource approval, cancellation, and final cwd/executable revalidation stay
in their existing order. No permission denial, correction or user-decline catch
was added. Directory arguments still require explicit literal paths.

Prefix arities remain those in source `parse.ts`. Assignment prefixes and call
operators must not disappear from fallback resources. If a source-shaped fallback
contains literal `*` or `?`, it is not offered as a saved grant: the current
permission wildcard algebra has no literal escape. The command still needs normal
permission approval. The scanner never adds a blanket wildcard grant.
For POSIX prefix selection, words after the first trailing redirect are excluded,
matching portable `redirectWordCount`; all actual arguments remain available to
the stricter directory validation rather than being silently discarded there.

Conservative differences remain: POSIX declaration builtins also require shell
permission here (source `scanPortable` skips declaration resources but retains
their substitutions). Redirects without a command name and enclosing compound
redirects require an additional raw shell resource rather than disappearing from
the source's command-only walk. Such resources receive only an exact-text saved
grant when representable, never a fabricated executable prefix or blanket wildcard.
Dynamic directory arguments and dynamic executable names are rejected, not guessed
from assignments. Here-document logical-line normalization can change a nested
substitution's spelling relative to the source portable scanner's raw-body scan;
the enclosing permission resource always retains the original text.

PowerShell expression approval is deliberately stricter than source's portable
command-only walk. Member/index/type/array expressions, parameter declarations,
typed catch constraints and named splats retain raw resources with no fabricated
executable words. Nested command pipelines retain their normal resources too. A
method call is not classified as harmless merely because it contains no command
node. Enclosing assignments/composite expressions retain their complete text when
they contain these resources. Commands containing the new expression/splat argument
forms offer only exact-text saved grants (or none when literal wildcards make that
unrepresentable), not a new prefix wildcard. Ordinary command-prefix arities remain
unchanged. Permission denies/corrections/declines follow the existing service path.
POSIX array argument forms and PowerShell stop-parsing commands also offer only
exact-text saved grants, or none when literal wildcards prevent an exact grant.
This is deliberately narrower than the portable source's conventional prefix save;
ordinary prefix arities and raw-resource grouping are unchanged. A `--%` tail never
provides a way to infer a dynamic executable/directory. Background operators outside
literal tails remain blocked. Native programs can interpret their arguments, but
this scanner does not treat their OS descendants as tracked or managed jobs.

These are permissions for source text, not a CLR sandbox or value-based guarantee.
The scanner does not resolve a method's receiver, type aliases, runtime argument
values, splatted keys or overloads. All new constructs are classified without
calling PowerShell, reflection, a type loader or an expression evaluator.

## Explicit remaining boundaries

Background operators remain unsupported. Nested POSIX array constructors,
array-valued element assignments, unquoted bracket-leading glob elements and some
ambiguous subscript-assignment lookaheads remain restricted. Array kinds are not
tracked: quoted/escaped keys containing expansion-like text are rejected when their
meaning could depend on indexed versus associative interpretation, rather than
rescanning literal key data as a command. This is stricter than source's generic
arithmetic-subscript walker. Array element-boundary comments are skipped explicitly.
PowerShell stop markers in redirect targets and Unicode-escape spellings of the
marker remain rejected. General concatenated quoting is still restricted; only
recognized generic stop-marker spellings use the dedicated decoder.
PowerShell dynamic/quoted member names, null-conditional
members, generic method invocation, class/enum definitions, assembly-qualified
types, multidimensional array type notation, member/index postfix updates,
computed hashtable keys and scoped or computed splats remain rejected. Parameter defaults and member/index assignment
RHSs require expressions; wrap command pipelines in parentheses. Attribute/type
names are structural identifiers only, not checked against loaded assemblies.
Parameter/attribute placement and full shell early-error validation remain outside
this bounded scanner. Single quotes in an enclosing
double-quoted POSIX parameter fallback are rejected because their quote semantics
differ from ordinary word quoting. Extended case glob syntax, PowerShell switch
file input, arbitrary file-descriptor expressions, concatenated PowerShell tokens,
Unicode shell syntax and several literal/operator forms remain restricted.
Arithmetic scanning follows source expansion discovery, not a full numeric grammar
validator: balanced tokens may still fail in the selected shell. Single quotes in
arithmetic modes do not suppress substitution discovery, matching source
`bashExpansion`. This port is stricter about misplaced closing delimiters and
semicolons: arithmetic `for` requires two top-level separators; other arithmetic
spans reject semicolons. It does not inspect values obtained indirectly from shell
variables for additional executable syntax or promise full shell/runtime parity.
Unclassified or unbalanced syntax fails before an invocation is prepared; it is
never passed to a shell as a fallback parsing strategy.

Input is limited to 65,536 characters and structural/substitution nesting to 32
levels. One invocation-owned work counter is shared by original source, decoded
backticks and expandable here-body rescans. It uses the source's 65,536 × 32 ceiling,
charging source/rescan lengths plus scanner checkpoints; this is conservatively
stricter than source's rescan-length-only accounting. Child scanners cannot reset
the allowance. Scanning checks cancellation. Raw spelling is retained for resources;
decoded backtick bodies follow the portable source's nested-resource convention.

Validation is limited to the pinned .NET 11 Core build with isolated artifacts.
No shell command samples, parser tests, native/PowerShell processes, or runtime
conformance checks were executed. A successful build proves C# compilation only.
Complete source/runtime conformance remains unverified.
