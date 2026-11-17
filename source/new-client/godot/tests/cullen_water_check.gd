extends SceneTree
## Actual water meshes and height queries, no server and no full-zone fixture load.
## --headless --path godot -s res://tests/cullen_water_check.gd -- --data=<staged zone205>

var failures := 0
var checks := 0


func check(ok: bool, message: String) -> void:
	checks += 1
	if not ok:
		failures += 1
		print("[cullen] FAIL ", message)


func _initialize() -> void:
	await process_frame
	var staged := "res://content/zones/zone205"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--data="):
			staged = arg.trim_prefix("--data=")
	# Load after the autoloads exist, as load_check.gd does (Server / Perf dependencies).
	var z = load("res://game/zone_loader.gd").new()
	root.add_child(z)
	z.dir = "res://content/zones/zone205"
	z.info = JSON.parse_string(FileAccess.get_file_as_string(staged.path_join("zone.json")))
	z._load_heights()
	z.water_mask = Image.load_from_file(staged.path_join("water.png"))
	var folders := {}
	for key in z.info.texture_sets:
		folders[key] = z.dir.path_join(z.info.texture_sets[key])
	z.materials = load("res://game/nif_materials.gd").new(folders, "original")
	z._build_water()
	var meshes: Array = z.find_children("Water_*", "MeshInstance3D", false, false)
	check(meshes.size() == 5, "all five lakes have actual meshes (got %d)" % meshes.size())
	for mesh in meshes:
		check((mesh as MeshInstance3D).mesh.get_surface_count() > 0, "%s has triangles" % mesh.name)
	var wet := [[65, 80], [130, 80], [110, 170], [60, 175], [190, 180]]
	for point in wet:
		var x: float = (float(point[0]) + 0.5) * 6.4
		var y: float = (float(point[1]) + 0.5) * 6.4
		check(is_equal_approx(z.water_level_at(x, y), 122.75), "lake water-height query %s" % str(point))
		check(z.water_level_at(x, y) - z.height_at(x, y) < 1.25, "period shallows remain wading depth %s" % str(point))
	for point in [[100, 80], [39, 80], [120, 20], [250, 250], [5, 5]]:
		var x: float = (float(point[0]) + 0.5) * 6.4
		var y: float = (float(point[1]) + 0.5) * 6.4
		check(z.water_level_at(x, y) == -INF, "land stays dry %s" % str(point))
	print("[cullen] %d checks; %d failures" % [checks, failures])
	z.free()
	quit(0 if failures == 0 else 1)
