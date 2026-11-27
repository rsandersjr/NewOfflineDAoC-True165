extends SceneTree
## The last AI option must be usable with a long list, a scaled HUD and a page resize.

var failed := false

func _initialize() -> void:
	call_deferred("_run")

func _check(ok: bool, label: String) -> void:
	print("[AI options] ", "PASS " if ok else "FAIL ", label)
	failed = failed or not ok

func _screen_rect(control: Control) -> Rect2:
	return control.get_global_transform_with_canvas() * Rect2(Vector2.ZERO, control.size)

func _run() -> void:
	root.size = Vector2i(1280, 720)
	var hud := Control.new()
	root.add_child(hud)
	var window: Control = load("res://game/companions/srv_companion_window.gd").new()
	var net: Node = load("res://game/companions/companion_net.gd").new()
	root.add_child(net)
	window.net = net
	hud.add_child(window)
	var page := Control.new()
	page.size = Vector2(470, 270)
	window.add_child(page)
	window._build_options(page)
	var scroll := page.get_child(0) as ScrollContainer
	await process_frame

	var rows := []
	for i in 20:
		rows.append({"kind": 41, "slot": 0, "chosen": 0, "names": ["Option %d" % i, "Automatic", "Defensive", "Off"]})
	rows.append({"kind": 55, "slot": 0, "chosen": 0, "names": ["Pet focus shield", "Off", "On (when pet is tanking)"]})
	net.options[0] = rows
	window._refresh_options()
	await process_frame
	await process_frame
	var bar := scroll.get_v_scroll_bar()
	var off := window._options_box.get_child(window._options_box.get_child_count() - 2) as Button
	var on := window._options_box.get_child(window._options_box.get_child_count() - 1) as Button
	_check(bar.visible and bar.max_value > bar.page and off.text == "OFF" and off.button_pressed
		and not on.button_pressed, "long list scrolls and the focus shield is Off")
	for ui_scale in [0.75, 1.0, 2.4]:
		root.size = Vector2i(3840, 2160) if ui_scale > 2 else Vector2i(1280, 720)
		hud.scale = Vector2.ONE * ui_scale
		scroll.scroll_vertical = 0
		await process_frame
		var wheel := InputEventMouseButton.new()
		wheel.position = scroll.get_global_transform_with_canvas() * Vector2(8, 18)
		wheel.button_index = MOUSE_BUTTON_WHEEL_DOWN
		wheel.pressed = true
		root.push_input(wheel, true)
		await process_frame
		_check(scroll.scroll_vertical > 0, "wheel scrolls at scale %.2f" % ui_scale)
		on.grab_focus()
		await process_frame
		await process_frame
		var viewport := _screen_rect(scroll)
		var button := _screen_rect(on)
		if not viewport.grow(2.0).encloses(button):
			print("scroll=", scroll.scroll_vertical, " viewport=", viewport, " button=", button)
		_check(viewport.grow(2.0).encloses(button), "keyboard focus reveals the last option at scale %.2f" % ui_scale)
		on.release_focus()
	page.size.y = 240
	net.options[0][20].chosen = 1
	window._refresh_options()
	await process_frame
	await process_frame
	var chosen := window._options_box.get_child(window._options_box.get_child_count() - 1) as Button
	chosen.grab_focus()
	await process_frame
	_check(chosen.button_pressed and _screen_rect(scroll).grow(2.0).encloses(_screen_rect(chosen)),
		"last option remains accessible and shows On after refresh and resize")
	net.options[0] = [rows.back()]
	window._refresh_options()
	await process_frame
	await process_frame
	_check(not bar.visible and scroll.scroll_vertical == 0, "short list returns to the top")
	quit(1 if failed else 0)
