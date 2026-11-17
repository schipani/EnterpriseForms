"""F-E1 enforcement-removal mutations (A-1). apply <id> | list. Exact single-occurrence replacements; the driver restores the file."""
import json
import sys

STORE = 'src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs'
COMMAND = 'src/authority/AuthorizationAuthority.Api/Composition/ContentAcceptanceFloorCommand.cs'
HISTORY = '                    await context.Database.ExecuteSqlRawAsync(ContentAcceptanceWriteFence.HistoryLockSql, cancellationToken).ConfigureAwait(false);\n'
OPS = '                    await context.Database.ExecuteSqlRawAsync(ContentAcceptanceWriteFence.LockSql, cancellationToken).ConfigureAwait(false);\n'

MUTATIONS = {
    'MA-1': ('remove the fence history lock (the pre-A-1 order)', STORE, HISTORY + OPS, OPS),
    'MA-2': ('take the history lock after the operations lock (inverted order)', STORE, HISTORY + OPS, OPS + HISTORY),
    'MA-3': ('remove the floor command session lock timeout', COMMAND, '            context.Database.ExecuteSqlRaw(SessionLockTimeoutSql);\n', ''),
}

if sys.argv[1] == 'list':
    print(json.dumps({k: v[0] for k, v in MUTATIONS.items()}))
    sys.exit(0)

_, path, old, new = MUTATIONS[sys.argv[2]]
text = open(path, encoding='utf-8', newline='').read()
assert text.count(old) == 1, (sys.argv[2], text.count(old))
open(path, 'w', encoding='utf-8', newline='').write(text.replace(old, new))
print(path)
