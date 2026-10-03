"""The helper a Python migration step (migrations/NNNN-name.py) imports - it talks to the LIVE engine of `fiesta migrate`.

    from fiesta_step import query, execute, executemany

    rows = query('SELECT "ID", "InxName" FROM "ItemInfo" WHERE "Class" = @c', {'c': 5})     # list of dicts
    n = execute('UPDATE "ItemInfoServer" SET "DropGroupA" = @g WHERE "ID" = @id', {'g': 'x', 'id': 1})
    n = executemany('INSERT INTO "T" ("A", "B") VALUES (@a, @b)', [{'a': 1, 'b': 2}, ...])      # one transaction

Parameters are NAMED (@name / :name / $name; a plain key gets "@"). A step reads and writes tables only - no files,
no other inputs - and prints whatever it wants to report (print() goes to the migrate log). It is SQL's last resort:
see the silver rule in the project's CLAUDE.md.
"""
import json
import sys

_PROTOCOL = sys.stdout
sys.stdout = sys.stderr            # the step's own print()s go to the log, the protocol keeps the real stdout
_REPLIES = sys.stdin


def _call(op, **kw):
    _PROTOCOL.write('@@fiesta ' + json.dumps(dict(op=op, **kw), ensure_ascii=False) + '\n')
    _PROTOCOL.flush()
    line = _REPLIES.readline()
    if not line:
        raise RuntimeError('fiesta_step: collab closed the session')
    r = json.loads(line)
    if not r.get('ok'):
        raise RuntimeError('fiesta_step: %s failed: %s' % (op, r.get('error')))
    return r


def query(sql, params=None):
    """rows as dicts (column -> value)"""
    r = _call('query', sql=sql, params=params or {})
    cols = r['columns']
    return [dict(zip(cols, row)) for row in r['rows']]


def query_rows(sql, params=None):
    """(columns, rows as lists) - cheaper for big tables"""
    r = _call('query', sql=sql, params=params or {})
    return r['columns'], r['rows']


def execute(sql, params=None):
    return _call('exec', sql=sql, params=params or {})['affected']


def executemany(sql, rows):
    rows = list(rows)
    total = 0
    for i in range(0, len(rows), 5000):              # bounded request lines
        total += _call('many', sql=sql, rows=rows[i:i + 5000])['affected']
    return total
