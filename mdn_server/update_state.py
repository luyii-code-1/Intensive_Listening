"""Per-device update assignments and retained deployment history."""
from datetime import datetime

STATUS_NAMES = {"waiting": "等待接收", "received": "已接收", "downloading": "下载中", "ready": "等待安装", "deferred": "关闭窗口后安装",
                "installing": "安装中", "success": "更新成功", "failed": "更新失败", "skipped": "未执行", "disabled": "设备已禁用"}


def initialize_updates(conn, store):
    migrated = conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='update_assignments'").fetchone()
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS update_assignments(device_id TEXT PRIMARY KEY, task_id TEXT);
        CREATE TABLE IF NOT EXISTS update_targets(task_id TEXT, device_id TEXT, name TEXT, state TEXT NOT NULL DEFAULT 'active', PRIMARY KEY(task_id,device_id));
    """)
    if migrated:
        return
    policies = {r["scope"]: store.decode(r["payload"]) for r in conn.execute("SELECT scope,payload FROM policies")}
    revisions = {r["scope"]: r["revision"] for r in conn.execute("SELECT scope,revision FROM policies")}
    def published(update):
        row = conn.execute("SELECT created FROM update_tasks WHERE id=?", (update["id"],)).fetchone()
        return row[0] if row else 0
    for device in conn.execute("SELECT id,name FROM devices").fetchall():
        own = policies.get(device["id"], {})
        candidates = [update for update in (policies.get("all", {}).get("update"), own.get("update")) if update]
        update = max(candidates, key=published) if candidates else None
        if "update" in own and own["update"] is None and (not update or revisions.get(device["id"], 0) / 1000000 >= published(update)):
            update = None
        conn.execute("INSERT INTO update_assignments VALUES(?,?)", (device["id"], update.get("id") if update else None))
    conn.execute("INSERT INTO update_targets SELECT t.id,d.id,d.name,CASE WHEN a.task_id=t.id THEN 'active' ELSE 'ended' END FROM update_tasks t JOIN devices d ON t.scope='all' OR t.scope=d.id LEFT JOIN update_assignments a ON a.device_id=d.id")


def assign_update(conn, scope, update):
    devices = conn.execute("SELECT id,name FROM devices" + (" WHERE id=?" if scope != "all" else ""), () if scope == "all" else (scope,)).fetchall()
    for device in devices:
        conn.execute("UPDATE update_targets SET state='replaced' WHERE device_id=? AND state='active' AND task_id<>?", (device["id"], update["id"]))
        conn.execute("INSERT INTO update_assignments VALUES(?,?) ON CONFLICT(device_id) DO UPDATE SET task_id=excluded.task_id", (device["id"], update["id"]))
        conn.execute("INSERT INTO update_targets VALUES(?,?,?,'active') ON CONFLICT(task_id,device_id) DO UPDATE SET state='active'", (update["id"], device["id"], device["name"]))


def effective_update(conn, identifier, store):
    assigned = conn.execute("SELECT task_id FROM update_assignments WHERE device_id=?", (identifier,)).fetchone()
    if assigned is None:
        global_row = conn.execute("SELECT payload FROM policies WHERE scope='all'").fetchone()
        update = store.decode(global_row[0]).get("update") if global_row else None
        conn.execute("INSERT INTO update_assignments VALUES(?,?)", (identifier, update.get("id") if update else None))
        if update:
            name = conn.execute("SELECT name FROM devices WHERE id=?", (identifier,)).fetchone()[0]
            conn.execute("INSERT OR IGNORE INTO update_targets VALUES(?,?,?,'active')", (update["id"], identifier, name))
        return update
    if assigned[0] is None:
        return None
    task = conn.execute("SELECT payload FROM update_tasks WHERE id=?", (assigned[0],)).fetchone()
    return store.decode(task[0]) if task else None


def stop_updates(conn, scope):
    selected = "SELECT device_id FROM update_assignments" + (" WHERE device_id=?" if scope != "all" else "")
    args = () if scope == "all" else (scope,)
    conn.execute("UPDATE update_targets SET state='stopped' WHERE state='active' AND device_id IN (" + selected + ")", args)
    conn.execute("UPDATE update_assignments SET task_id=NULL" + (" WHERE device_id=?" if scope != "all" else ""), args)


def active_ids(conn, store):
    ids = {row[0] for row in conn.execute("SELECT DISTINCT a.task_id FROM update_assignments a JOIN devices d ON d.id=a.device_id WHERE a.task_id IS NOT NULL")}
    row = conn.execute("SELECT payload FROM policies WHERE scope='all'").fetchone()
    update = store.decode(row[0]).get("update") if row else None
    if update:
        ids.add(update["id"])
    return ids


def update_dashboard(conn, store, scope):
    args = () if scope == "all" else (scope,)
    current = [dict(row) for row in conn.execute("""SELECT d.id device_id,coalesce(nullif(d.alias,''),d.name) device_name,d.version installed_version,d.enabled,
        t.id,t.version,t.mode,t.created,coalesce(r.status,'waiting') status,coalesce(r.progress,0) progress,r.message,r.at
        FROM devices d LEFT JOIN update_assignments a ON a.device_id=d.id LEFT JOIN update_tasks t ON t.id=a.task_id
        LEFT JOIN update_receipts r ON r.task_id=t.id AND r.device_id=d.id""" + (" WHERE d.id=?" if scope != "all" else "") + " ORDER BY d.name", args)]
    for row in current:
        if row["id"] and not row["enabled"]:
            row["status"] = "disabled"
        row["status_name"] = STATUS_NAMES.get(row["status"], row["status"]) if row["id"] else "暂无任务"
        row["at_text"] = date_text(row["at"] or row["created"]) if row["id"] else "—"
    tasks = [dict(row) for row in conn.execute("SELECT t.* FROM update_tasks t" + (" WHERE EXISTS(SELECT 1 FROM update_targets x WHERE x.task_id=t.id AND x.device_id=?)" if scope != "all" else "") + " ORDER BY created DESC LIMIT 50", args)]
    active = active_ids(conn, store)
    for task in tasks:
        task.pop("payload", None)
        task["active"] = task["id"] in active
        task["created_text"] = date_text(task["created"])
        task["targets"] = [dict(row) for row in conn.execute("""SELECT x.device_id,coalesce(nullif(d.alias,''),d.name,x.name) device_name,x.state,
            coalesce(r.status,'waiting') status,coalesce(r.progress,0) progress,r.message,r.at FROM update_targets x LEFT JOIN devices d ON d.id=x.device_id
            LEFT JOIN update_receipts r ON r.task_id=x.task_id AND r.device_id=x.device_id WHERE x.task_id=?""" + (" AND x.device_id=?" if scope != "all" else "") + " ORDER BY device_name", (task["id"],) + args)]
        for target in task["targets"]:
            target["status_name"] = STATUS_NAMES.get(target["status"], target["status"])
            target["state_name"] = {"active": "当前任务", "replaced": "已被新任务替换", "stopped": "已停止下发", "ended": "历史任务"}[target["state"]]
    assigned = [row for row in current if row["id"]]
    summary = {"assigned": len(assigned), "waiting": sum(row["status"] == "waiting" for row in assigned),
               "installing": sum(row["status"] in ("received", "downloading", "ready", "deferred", "installing") for row in assigned),
               "success": sum(row["status"] == "success" for row in assigned), "failed": sum(row["status"] in ("failed", "skipped") for row in assigned)}
    replacements = sum(row["status"] not in ("success", "skipped") for row in assigned)
    return {"update_devices": current, "update_tasks": tasks, "update_summary": summary, "update_replacements": replacements}


def date_text(stamp):
    return datetime.fromtimestamp(stamp).astimezone().strftime("%m-%d %H:%M:%S")
