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
	await check_enchant_picker()
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

func check_enchant_picker() -> void:
	var viewport := get_tree().root
	if "--borderless-probe" in OS.get_cmdline_user_args():
		viewport.borderless = true
		check(viewport.borderless, "borderless window is active")
	var hud := Control.new()
	viewport.add_child(hud)
	var panel := Enchant.new()
	hud.add_child(panel)
	var labels := []
	for i in 80:
		labels.append("BONUS %d" % i)
	labels[79] = "TWO HANDED SPECIFICATION FOCUS"
	var picked := [-1]
	var button := panel._make_choice(panel, labels, 146.0, func(i: int): picked[0] = i)
	button.size = Vector2(146, 18)
	button.set_item_tooltip(79, "Last option tooltip")
	for resolution in [Vector2i(640, 480), Vector2i(1280, 720), Vector2i(1920, 1080), Vector2i(3840, 2160)]:
		for ui_scale in [0.6, 1.0, 1.6]:
			viewport.size = resolution
			hud.scale = Vector2.ONE * resolution.y / 600.0 * 0.78 * ui_scale
			button.position = Vector2(viewport.size) / hud.scale - button.size - Vector2(10, 10)
			button.select(0)
			panel._show_choice(button)
			await get_tree().process_frame
			await get_tree().process_frame
			var popup: Panel = panel._choice_popup
			var list: ItemList = panel._choice_list
			var scroll := list.get_v_scroll_bar()
			var fitted := viewport.get_visible_rect().encloses(Rect2(popup.position, popup.size * popup.scale))
			scroll.value = scroll.min_value
			await get_tree().process_frame
			var top_visible := scroll.value <= scroll.min_value + 1 and list.get_item_rect(0).end.y <= list.size.y
			var wheel := InputEventMouseButton.new()
			wheel.position = list.get_global_transform_with_canvas() * Vector2(25, 25)
			wheel.button_index = MOUSE_BUTTON_WHEEL_DOWN
			wheel.pressed = true
			viewport.push_input(wheel, true)
			await get_tree().process_frame
			var wheel_moves := scroll.value > 0
			scroll.value = scroll.max_value
			await get_tree().process_frame
			var bottom_visible := scroll.value > 0 and list.get_item_rect(79).end.y - scroll.value <= list.size.y
			check(fitted and top_visible and bottom_visible and scroll.visible and list.get_item_count() == 80
				and list.get_item_tooltip(79) == "Last option tooltip", "bounded bonus picker %s at %.1f" % [resolution, ui_scale])
			var last := list.get_item_rect(79).get_center() - Vector2(0, scroll.value)
			var click := InputEventMouseButton.new()
			click.position = list.get_global_transform_with_canvas() * last
			click.button_index = MOUSE_BUTTON_LEFT
			click.pressed = true
			viewport.push_input(click, true)
			click.pressed = false
			viewport.push_input(click, true)
			check(picked[0] == 79 and button.selected == 79 and not popup.is_visible_in_tree(), "select final bonus")
	var tier := panel._make_choice(panel, ["+1 IMBUE", "+2 IMBUE", "+3 IMBUE"], 146.0, func(i: int): picked[0] = i)
	tier.position = button.position
	tier.size = button.size
	panel._show_choice(tier)
	await get_tree().process_frame
	hud.scale *= 0.7
	viewport.size = Vector2i(1024, 768)
	await get_tree().process_frame
	await get_tree().process_frame
	check(panel._choice_list.get_item_count() == 3 and
		viewport.get_visible_rect().encloses(Rect2(panel._choice_popup.position, panel._choice_popup.size * panel._choice_popup.scale)),
		"open gem tiers refit on a live resize and UI-scale change")
	panel._choice_list.item_activated.emit(2)
	check(tier.selected == 2 and picked[0] == 2, "keyboard activates gem tier")
	panel._show_choice(tier)
	await get_tree().process_frame
	var cancel := InputEventKey.new()
	cancel.keycode = KEY_ESCAPE
	cancel.pressed = true
	viewport.push_input(cancel, true)
	check(not panel._choice_overlay.visible, "Escape dismisses the picker")
	hud.queue_free()


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
