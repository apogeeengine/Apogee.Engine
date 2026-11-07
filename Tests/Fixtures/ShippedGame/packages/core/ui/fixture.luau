--!nonstrict
-- The fixture document's script: a function its onclick calls, and a mark that the document's scripts ran (CI checks it).
function clicked(event)
	print("fixture document clicked", event.type)
end

assert(fs.write_text("user://document.txt", "document ran"))
