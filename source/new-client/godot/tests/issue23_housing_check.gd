extends SceneTree
## Headless model/state checks; no server or player database required.
var failures := 0

class MockViewer extends Node:
	var sky: Node

class MockWorld extends Node3D:
	var OBJECT_PICK := 16
	var viewer := MockViewer.new()
	func scene_pos(x: float, y: float, z: float) -> Vector3:
		return Vector3(x, z, y) * 0.025


func check(ok: bool, text: String) -> void:
	if not ok:
		failures += 1
		printerr("[housing] FAIL ", text)


func _initialize() -> void:
	await process_frame
	var figure = load("res://game/fig3_figure.gd")
	var world := MockWorld.new()
	world.viewer.sky = load("res://game/sky_system.gd").new()
	world.add_child(world.viewer)
	world.viewer.add_child(world.viewer.sky)
	root.add_child(world)
	var housing = load("res://game/housing/housing.gd").new()
	world.add_child(housing)
	var view = load("res://game/housing/house_view.gd").new()
	world.add_child(view)
	view.setup(world, housing)
	housing.on_packet("house", {"number": 2001, "model": 8, "name": "Test", "x": 0, "y": 0, "z": 0, "heading": 0,
			"emblem": 1025, "banner": false, "shield": false, "porch": true, "porch_roof_color": 10})
	var exterior: Node = view.exteriors[2001].scene
	check(not view._helper(exterior, "shields").visible, "unbought shield hidden")
	check(not view._helper(exterior, "banners").visible, "unbought banner hidden")
	var tent := view._helper(exterior, "tent").find_children("*", "MeshInstance3D", true, false)[0] as MeshInstance3D
	check((tent.get_active_material(0) as ShaderMaterial).get_shader_parameter("tint") == figure.dye(10), "awning tint applied")
	housing.on_packet("house_enter", {"number": 2001, "model": 8, "x": 0, "y": 0, "door": 0, "rugs": [10, 11, 12, 13],
			"banner": false, "shield": true, "emblem": 1025})
	check(world.viewer.sky.housing_inside, "rain disabled inside")
	var ladder: Node = view.interior.get_node_or_null("ClimbBodies")
	check(ladder != null and ladder.get_child_count() > 0, "mansion has climb collision")
	check(view.interior.get_meta("fire_particles", []).size() > 0, "period fireplace particle systems created")
	var inn: Node = view.interior.get_meta("scene")
	check(view._helper(inn, "shields").visible and not view._helper(inn, "banners").visible, "interior purchase flags applied")
	for i in 4:
		var rug := view._helper(inn, "carpet%03d" % (i + 1)).find_children("*", "MeshInstance3D", true, false)[0] as MeshInstance3D
		check((rug.get_active_material(0) as ShaderMaterial).get_shader_parameter("tint") == figure.dye(10 + i), "carpet %d tint" % i)
	housing.on_packet("house_exit", {"number": 2001})
	check(not world.viewer.sky.housing_inside, "outdoor weather restored")
	await process_frame
	world.free()
	print("[housing] failures=", failures)
	quit(1 if failures else 0)
