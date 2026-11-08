--!nocheck
-- Deliberately broken: the game logs the error and runs without this mod.
local settings = nil
print(settings.volume) -- a bug: settings is nil
