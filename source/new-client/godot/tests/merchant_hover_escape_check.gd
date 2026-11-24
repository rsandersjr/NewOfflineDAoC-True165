extends SceneTree
## Run headless with -s res://tests/merchant_hover_escape_check.gd. Uses the real merchant rows and Escape
## handlers with synthetic stock and pointer events, without a server or a save.

var _failed := 0
var _player_data: Node


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	root.size = Vector2i(800, 600)
	root.gui_embed_subwindows = true
	root.notification(Viewport.NOTIFICATION_VP_MOUSE_ENTER)
	_player_data = root.get_node("PlayerData")
	_player_data.level = 10

	var item_db = load("res://game/ui/item_db.gd")
	for i in 2:
		var id := "srvm:0:%d" % i
		item_db.add_runtime(id, {"id": id, "name": "Test Sword %d" % i, "level": 10,
			"object_type": 11, "type_damage": 2, "dps_af": 25, "spd_abs": 32, "price": 100})

	var ui = _without_scene_setup("res://game/ui/game_ui.gd")
	root.add_child(ui)
	ui.options = load("res://game/ui/options_window.gd").new()
	ui.options.hide()
	ui.add_child(ui.options)
	ui.options.setup("options", "options")
	ui.options.build()

	var shop = load("res://game/ui/merchant_window.gd").new()
	shop.setup("merchant", "merchant")
	root.add_child(shop)
	shop.build()
	shop.open_server("Test Merchant", {0: ["srvm:0:0", "srvm:0:1"]}, 0)
	ui.merchant = shop
	ui.clear_target = func(): _player_data.set_target({})
	_player_data.set_target({"name": "Test Merchant"})

	var first: Control = shop._rows[0]
	var second: Control = shop._rows[1]
	await process_frame
	await _hover(first)
	_check(_popup_visible(first), "merchant row shows the original tooltip")
	_escape()
	await process_frame
	_check(_player_data.target.is_empty(), "one Escape untargets with a visible shop tooltip")
	_check(shop.visible and _popup_visible(first), "shop and its tooltip stay open")
	await _hover(second)
	_check(_popup_visible(second), "another row shows its tooltip without a target")
	await _hover(first)
	_check(_popup_visible(first), "original row can be hovered again without a target")
	_player_data.set_target({"name": "Unrelated NPC"})
	await _hover(second)
	_check(_popup_visible(second), "another target does not affect the shop tooltip")

	# Run the existing world input handler too: Esc must return a held backpack item first.
	var world = _without_scene_setup("res://game/zone_viewer.gd")
	root.add_child(world)
	first.force_drag({"kind": "item", "slot": 40}, null)
	_check(root.gui_is_dragging(), "test item is on the pointer")
	_escape()
	await process_frame
	_check(not root.gui_is_dragging() and _player_data.target.get("name") == "Unrelated NPC",
		"Escape cancels a held item before untargeting")
	print("Merchant hover failures: ", _failed)
	quit(1 if _failed else 0)


func _without_scene_setup(path: String) -> Node:
	var script := GDScript.new()
	script.source_code = "extends %s\nfunc _ready() -> void:\n\tset_process(false)\n\tset_physics_process(false)\n\tset_process_unhandled_input(false)\n" % JSON.stringify(path)
	if script.reload() != OK:
		quit(2)
		return null
	return script.new()


func _check(ok: bool, description: String) -> void:
	if not ok:
		_failed += 1
	print("%s %s" % ["PASS" if ok else "FAIL", description])


func _hover(row: Control) -> void:
	var motion := InputEventMouseMotion.new()
	motion.position = row.get_global_rect().position + Vector2(12, 8)
	motion.global_position = motion.position
	root.push_input(motion, true)
	await create_timer(0.8).timeout


func _popup_visible(row: Control) -> bool:
	for child in row.get_children(true):
		if child is PopupPanel and child.visible:
			return true
	return false


func _escape() -> void:
	var key := InputEventKey.new()
	key.keycode = KEY_ESCAPE
	key.physical_keycode = KEY_ESCAPE
	key.pressed = true
	root.push_input(key, true)
