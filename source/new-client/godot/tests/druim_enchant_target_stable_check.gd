extends Node

const Enchant := preload("res://game/ui/craft_enchant_panel.gd")
const Viewer := preload("res://game/zone_viewer.gd")

class ProbeManager extends NpcManager:
	func ground_at(_p: Vector3) -> float:
		return 0.0

class ProbePlayer extends Player:
	func _ready() -> void:
		set_physics_process(false)

class ProbeViewer extends Viewer:
	func _ready() -> void:
		set_process(false)
		set_physics_process(false)

var failed := 0

func check(good: bool, text: String) -> void:
	print("[fixes] ", text, " ", "PASS" if good else "FAIL")
	if not good:
		failed += 1

func _ready() -> void:
	await get_tree().process_frame
	get_tree().root.size = Vector2i(560, 400)
	var panel := Enchant.new()
	get_tree().root.add_child(panel)
	panel.set_process(false)
	var button := OptionButton.new()
	panel.add_child(button)
	for i in 80:
		button.add_item("BONUS %d" % i)
	panel._limit_choice_popup(button)
	var menu := button.get_popup()
	button.show_popup()
	await get_tree().process_frame
	await get_tree().process_frame
	print("[fixes] menu size=", menu.size, " cap=", menu.max_size)
	check(menu.size.y <= get_tree().root.size.y and menu.item_count == 80 and menu.max_size.y <= 360, "bonus popup fits viewport with all items")
	var scroll := internal_scroll(menu)
	check(scroll != null and scroll.get_v_scroll_bar().max_value > scroll.get_v_scroll_bar().page, "bonus scrollbar can scroll")
	if scroll != null:
		menu.scroll_to_item(79)
		await get_tree().process_frame
		check(scroll.scroll_vertical > 0, "bonus popup reaches last item")
	menu.hide()
	panel.queue_free()
	var viewer := ProbeViewer.new()
	get_tree().root.add_child(viewer)
	var p := ProbePlayer.new()
	viewer.add_child(p)
	viewer.player = p
	var mgr := ProbeManager.new()
	viewer.add_child(mgr)
	mgr.set_process(false)
	mgr.set_physics_process(false)
	viewer.npcs = mgr
	var zone := ZoneLoader.new()
	viewer.add_child(zone)
	mgr.zone = zone
	var deck := StaticBody3D.new()
	deck.collision_layer = ZoneLoader.LAYER_FLOOR
	var box := BoxShape3D.new()
	box.size = Vector3(8, 0.4, 8)
	var collider := CollisionShape3D.new()
	collider.shape = box
	deck.add_child(collider)
	zone.add_child(deck)
	deck.position.y = 6.0
	var cam := Camera3D.new()
	viewer.add_child(cam)
	cam.current = true
	cam.position = Vector3(0, 1, 5)
	cam.look_at(Vector3(0, 1, -5))
	var front := make_npc(mgr, 0, {"player_like": true, "id": "bot"}, Vector3.ZERO)
	var service := make_npc(mgr, 1, {"role": "merchant", "id": "merchant"}, Vector3(0, 0, -3))
	await get_tree().physics_frame
	await get_tree().physics_frame
	await get_tree().process_frame
	var at := cam.unproject_position(Vector3(0, 1, 0))
	check(mgr.pick(cam, at, false) == front, "left-target nearest body")
	check(mgr.pick(cam, at) == service, "service interaction preference")
	viewer._server_steed = 42
	viewer._ride_drawn_y = 6.2
	p.global_position = Vector3(0, 7.5, 0)
	viewer._server_mount(0)
	check(absf(p.global_position.y - 6.5) < 0.04, "dismount lands on bridge collision")
	viewer._server_mount(0)
	check(absf(p.global_position.y - 6.5) < 0.04, "duplicate dismount keeps position")
	get_tree().quit(failed)

func internal_scroll(node: Node) -> ScrollContainer:
	for child in node.get_children(true):
		if child is ScrollContainer:
			return child
		var result := internal_scroll(child)
		if result != null:
			return result
	return null

func make_npc(mgr: NpcManager, i: int, spawn: Dictionary, pos: Vector3) -> Npc:
	var view := Npc.new()
	mgr.add_child(view)
	view.set_process(false)
	view.state = NpcState.new(i, spawn.merged({"pos": [pos.x, pos.y, pos.z], "heading": 0}), {})
	view.spawn = spawn
	view.position = pos
	var pick := StaticBody3D.new()
	pick.collision_layer = 1 << 9
	pick.set_meta("npc", view)
	var hitbox := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(1, 2, 1)
	hitbox.shape = box
	hitbox.position.y = 1.0
	pick.add_child(hitbox)
	view.add_child(pick)
	mgr.active[i] = view
	return view
