--!nonstrict
-- A mod: it writes only its own data folder.
assert(fs.write_text("user://moddata/patcher/ran.txt", "patcher ran"))
