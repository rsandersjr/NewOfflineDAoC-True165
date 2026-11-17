extends SceneTree
## Headless runtime attachment check against original 1.64 HELD/HELD2 data.
## --headless --path godot -s res://tests/classic_claw_alignment_check.gd
var failures := 0


func check(ok: bool, text: String) -> void:
	if not ok:
		failures += 1
		printerr("[claw] FAIL ", text)


func _initialize() -> void:
	await process_frame
	var weapons = load("res://game/weapon_models.gd")
	var raw = JSON.parse_string(FileAccess.get_file_as_string("res://content/classic-claw-grips.json"))
	check(raw is Array and raw.size() == 24, "24 original claw models")
	for row in raw:
		var model := int(row.model)
		for left in [false, true]:
			var weapon: Node3D = weapons.make(model, true, left)
			check(weapon != null, "%d loads" % model)
			if weapon == null:
				continue
			var frame = row.get("held2") if left and row.get("held2") != null else row.get("held")
			var expected := Transform3D.IDENTITY
			if frame is Array:
				expected = Transform3D(Basis(Vector3(frame[0], frame[1], frame[2]), Vector3(frame[3], frame[4], frame[5]),
						Vector3(frame[6], frame[7], frame[8])), Vector3(frame[9], frame[10], frame[11]))
			var mesh := weapon.get_child(0) as Node3D
			check(mesh.transform.is_equal_approx(expected * weapons.TO_NIF), "%d left=%s matches 1.64 grip" % [model, left])
			check(not mesh.find_children("*", "MeshInstance3D", true, false).is_empty(), "%d has geometry" % model)
			# The transformed mesh must cancel its original grip: hand-space origin
			# and basis are exact, rather than a guessed global quarter-turn.
			var attached: Transform3D = mesh.transform * weapons.TO_NIF.affine_inverse()
			check((attached * expected.affine_inverse()).is_equal_approx(Transform3D.IDENTITY), "%d attachment cancels grip" % model)
			weapon.free()
	print("[claw] 48 hand attachments checked; failures=", failures)
	quit(1 if failures else 0)
