--!strict
-- Steam as scripts see it. The Steam client is the player's, not ours: these hold on a machine without it, where the system is inactive, every call does nothing and the callbacks that
-- would carry a result say that it failed. (Against a real client: the opt-in tests of Apogee.Steam.Tests.)
local test = require("@engine/test")

-- Only in hosts that have the Steam library (the runtime does; the scripting tests' host does not).
if SteamSystem == nil then
	return
end

test.describe("steam", function()
	test.it("is inactive without Steam, and says so", function()
		local steam = SteamSystem.create_inactive()
		test.expect(steam.is_active).toBe(false)
		test.expect(steam.status ~= "").toBe(true)
		test.expect(steam.user_id).toBe("")
		test.expect(steam.app_id).toBe(0)
		test.expect(steam.is_cloud_enabled).toBe(false)
		steam:update()
	end)

	test.it("keeps achievements and stats inert", function()
		local steam = SteamSystem.create_inactive()
		test.expect(steam:achievement("ACH_WIN_ONE_GAME")).toBe(false)
		test.expect(steam:unlock_achievement("ACH_WIN_ONE_GAME")).toBe(false)
		test.expect(steam:stat_int("NumGames")).toBe(0)
		test.expect(steam:set_stat_int("NumGames", 2)).toBe(false)
		test.expect(steam:store_stats()).toBe(false)
		test.expect(#steam:achievement_names()).toBe(0)
	end)

	test.it("answers a leaderboard call with a failure", function()
		local steam = SteamSystem.create_inactive()
		local found: boolean? = nil
		local uploaded: boolean? = nil
		local entries = -1
		steam.leaderboard_found:connect(function(name: string, ok: boolean)
			found = ok
		end)
		steam.score_uploaded:connect(function(name: string, ok: boolean, rank: number)
			uploaded = ok
		end)
		steam.scores_downloaded:connect(function(name: string, users: { string }, ranks: { number }, scores: { number })
			entries = #users
		end)
		steam:find_leaderboard("fastest")
		steam:upload_score("fastest", 100)
		steam:download_scores("fastest", 1, 10)
		test.expect(found).toBe(false)
		test.expect(uploaded).toBe(false)
		test.expect(entries).toBe(0)
	end)

	test.it("lists no Workshop items and answers a subscription with a failure", function()
		local steam = SteamSystem.create_inactive()
		local subscribed: boolean? = nil
		steam.workshop_subscribed:connect(function(id: string, ok: boolean)
			subscribed = ok
		end)
		steam:subscribe("12345")
		test.expect(subscribed).toBe(false)
		test.expect(#steam:subscribed_items()).toBe(0)
		test.expect(steam:workshop_item_folder("12345")).toBe("")
	end)

	test.it("does nothing for the overlay, rich presence and the timeline", function()
		local steam = SteamSystem.create_inactive()
		steam:activate_overlay("friends")
		test.expect(steam:set_rich_presence("status", "Racing")).toBe(false)
		steam:clear_rich_presence()
		steam:add_timeline_event("Crash", "Hit a tree", "steam_death")
		test.expect(steam:owns_dlc(480)).toBe(false)
	end)
end)
