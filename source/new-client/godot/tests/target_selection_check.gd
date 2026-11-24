extends SceneTree
## Run headless with -s res://tests/target_selection_check.gd. Uses physics and the real selector.

var _failures := 0
var _manager: Node3D


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	root.size = Vector2i(800, 600)
	_manager = load("res://game/npcs/npc_manager.gd").new()
	root.add_child(_manager)
	_manager.set_process(false)
	_manager.set_physics_process(false)
	var player := Node3D.new()
	root.add_child(player)
	_manager.player = player
	var a := _npc(0, Vector3(0, 0, -8))
	var b := _npc(1, Vector3(0, 0, -12))
	var c := _npc(2, Vector3(0, 0, -16))
	var d := _npc(3, Vector3(0, 0, -20))
	var hidden := _npc(4, Vector3(0, 0, -2))
	hidden.state.stealthed = true
	for expected in [a, b, c, d, a]:
		var next: Node3D = _manager.nearest_enemy()
		_check(next == expected, "nearest-first cycle, excluding stealth")
		_manager.target = next
	_manager.target = d
	_check(_manager.nearest_enemy() == a, "manual retarget restarts the cycle")

	var camera := Camera3D.new()
	root.add_child(camera)
	camera.position.y = 1.0
	camera.current = true
	var front := _npc(5, Vector3(0, 0, -5))
	var back := _npc(6, Vector3(0, 0, -9))
	_part(front, Vector3(-1.2, 1.0, 0))
	_part(front, Vector3(1.2, 1.0, 0))
	_part(back, Vector3(0, 1.0, 0))
	front._build_pick()
	back._build_pick()
	var named := _npc(7, Vector3(2, 0, -8))
	var plate := Label3D.new()
	plate.font = ThemeDB.fallback_font
	plate.text = "Name pick"
	plate.font_size = 16
	named.add_child(plate)
	plate.position.y = 2.0
	named.set_meta("plate", [plate])
	named.set_meta("plate_shown", true)

	await physics_frame
	await physics_frame
	var middle := camera.unproject_position(Vector3(0, 1.0, -9))
	_check(_manager.pick(camera, middle, false) == back, "foreground bounds gap allows the visible rear NPC")
	var body := camera.unproject_position(Vector3(1.2, 1.0, -5))
	_check(_manager.pick(camera, body, false) == front, "solid foreground body still takes the click")
	_check(_manager.pick(camera, middle) == front, "right-click interaction still uses its existing reach")
	var name := camera.unproject_position(plate.global_position) - Vector2(0, 4)
	_check(_manager.pick(camera, name, false) == named, "visible overhead name still selects its NPC")
	print("Target selection failures: ", _failures)
	quit(1 if _failures else 0)


func _npc(index: int, pos: Vector3) -> Node3D:
	var n = load("res://game/npcs/npc.gd").new()
	var spawn := {"pos": [pos.x, pos.y, pos.z], "heading": 0, "flags": 0, "realm": 0, "level": 5}
	n.spawn = spawn
	n.state = load("res://game/npcs/npc_state.gd").new(index, spawn, {})
	n.position = pos
	n.set_process(false)
	_manager.add_child(n)
	_manager.active[index] = n
	return n


func _part(n, pos: Vector3) -> void:
	if n.model == null:
		n.model = Node3D.new()
		n.add_child(n.model)
	var piece := MeshInstance3D.new()
	piece.mesh = BoxMesh.new()
	piece.position = pos
	n.model.add_child(piece)


func _check(ok: bool, text: String) -> void:
	if not ok:
		_failures += 1
	print("%s %s" % ["PASS" if ok else "FAIL", text])
