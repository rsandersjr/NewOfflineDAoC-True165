extends SceneTree
## Headless: tab entry refreshes listings; replies do not loop or move items.
class FakeClient extends Node:
	var commands: Array = []
	var moves: Array = []
	func Command(text: String) -> void:
		commands.append(text)
	func MoveItem(from: int, to: int, count: int) -> void:
		moves.append([from, to, count])

var failures := 0
func check(ok: bool, label: String) -> void:
	print("[listing_refresh] ", "PASS " if ok else "FAIL ", label)
	if not ok:
		failures += 1

func _initialize() -> void:
	await process_frame
	var server = root.get_node("Server")
	var fake := FakeClient.new()
	root.add_child(fake)
	server.client = fake
	server.active = true
	server.chest_owner = "exchange"
	server.exchange_mode = "listings"
	server.proceeds = 0
	var ex = load("res://game/ui/exchange_window.gd").new()
	root.add_child(ex)
	ex.build()
	ex.show_tab("listings")
	ex._tab_listings.pressed.emit()
	check(fake.commands == ["&whisper manage my listings"], "already-open listings asks broker once")
	fake.commands.clear()
	server.listings = {150: "stale sold item", 151: "stale available item"}
	server._listings_packet({"items": [{"slot": 151, "name": "Available item", "model": 1,
		"level": 10, "value1": 0, "value2": 0, "hand": 0, "type_damage": 0,
		"object_type": 36, "price": 123}]}, true)
	check(not server.listings.has(150) and server.listings.has(151), "fresh reply removes sold item and retains available item")
	check(fake.commands.is_empty(), "server reply does not request another refresh")
	ex.show_tab("browse")
	ex._tab_listings.pressed.emit()
	check(fake.commands == ["&whisper manage my listings"], "switching back refreshes despite stale mode")
	fake.commands.clear()
	ex._tab_listings.pressed.emit()
	check(fake.commands == ["&whisper manage my listings"], "another click requests a new snapshot")
	check(fake.moves.is_empty(), "tab refresh never transfers inventory")
	server.active = false
	fake.commands.clear()
	ex._tab_listings.pressed.emit()
	check(fake.commands.is_empty(), "offline tab does not send a broker command")
	server.client = null
	ex.free()
	fake.free()
	quit(failures)
