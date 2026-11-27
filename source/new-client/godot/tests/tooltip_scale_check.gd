extends SceneTree
## Measures the actual Godot hover windows, including custom spell and item details.

class HoverControl extends Control:
	var make_tip: Callable

	func _make_custom_tooltip(_text: String) -> Object:
		return make_tip.call() if make_tip.is_valid() else null

var failures := 0

func _initialize() -> void:
	call_deferred("_run")

func check(ok: bool, label: String) -> void:
	print("[tooltip] ", "PASS " if ok else "FAIL ", label)
	if not ok:
		failures += 1

func _move_mouse(point: Vector2) -> void:
	var motion := InputEventMouseMotion.new()
	motion.position = point
	motion.global_position = point
	root.push_input(motion, true)

func _find_label(node: Node, caption: String) -> Label:
	if node is Label and node.text == caption and node.is_visible_in_tree():
		return node
	for child in node.get_children(true):
		var found := _find_label(child, caption)
		if found:
			return found
	return null

func _run() -> void:
	if "--borderless-probe" in OS.get_cmdline_user_args():
		root.borderless = true
		root.gui_embed_subwindows = true
	var tip: Script = load("res://game/ui/item_tooltip.gd")
	var hud := Control.new()
	hud.theme = UiArt.tooltip_theme()
	root.add_child(hud)
	var spell := HoverControl.new()
	spell.make_tip = func(): return tip.text_box("Focus spell", ["Damage: 123"])
	var item := HoverControl.new()
	item.make_tip = func(): return tip.make({"name": "Scale coif", "object_type": 35, "level": 1, "dps_af": 40, "spd_abs": 27})
	var plain := HoverControl.new()
	var captions := ["FOCUS SPELL", "SCALE COIF", "Plain tooltip"]
	for entry in [spell, item, plain]:
		entry.size = Vector2(100, 21)
		entry.tooltip_text = "Plain tooltip"
		hud.add_child(entry)
	for dimensions in [Vector2i(1280, 720), Vector2i(1920, 1080), Vector2i(3840, 2160)]:
		root.size = dimensions
		await process_frame
		var hud_scale := root.get_visible_rect().size.y / 600.0 * 0.78
		hud.scale = Vector2.ONE * hud_scale
		UiArt.set_tooltip_scale(hud.theme, hud_scale)
		var points := [spell, item, plain]
		for i in points.size():
			var entry: Control = points[i]
			entry.position = Vector2(root.size) / hud.scale - Vector2(125, 107 - 28 * i)
			_move_mouse(Vector2(5, 5))
			await process_frame
			_move_mouse(entry.get_global_transform_with_canvas() * Vector2(25, 10))
			await create_timer(0.9).timeout
			var label := _find_label(root, captions[i])
			var expected := maxi(10, roundi(13.0 * hud_scale / 1.404))
			check(label != null and label.get_theme_font_size("font_size") == expected,
				"%s font %d at %s" % [captions[i], expected, root.size])
			if label != null:
				var popup := label.get_window()
				check(Rect2i(Vector2i.ZERO, root.size).encloses(Rect2i(popup.position, popup.size)),
					"%s tooltip inside viewport" % captions[i])
			if i == 0 and dimensions.x == 3840:
				UiArt.set_tooltip_scale(hud.theme, hud_scale * 1.2)
				await process_frame
				await process_frame
				check(label != null and is_instance_valid(label) and label.get_theme_font_size("font_size") == roundi(expected * 1.2),
					"open tooltip follows the interface scale")
				UiArt.set_tooltip_scale(hud.theme, hud_scale)
	_move_mouse(Vector2(5, 5))
	await process_frame
	var long_detail := "A longer spell detail line ".repeat(25).strip_edges()
	spell.make_tip = func(): return tip.text_box("Focus spell", [long_detail])
	_move_mouse(spell.get_global_transform_with_canvas() * Vector2(25, 10))
	await create_timer(0.9).timeout
	var detail := _find_label(root, long_detail.to_upper())
	check(detail != null and detail.autowrap_mode == TextServer.AUTOWRAP_WORD_SMART
		and detail.size.x <= root.size.x * 0.7 + 1, "long delve text wraps within the viewport")
	quit(1 if failures else 0)
