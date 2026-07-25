#include "script_coordinator.h"

#include <gtest/gtest.h>

#include <atomic>
#include <string>

namespace
{
[[nodiscard]] std::string snapshot_with_script(const std::string_view source, const std::string_view source_hash)
{
    return "{\"schemaVersion\":8,\"activeProfileSetId\":\"set-a\",\"globalScript\":null,"
           "\"profileSets\":[{\"id\":\"set-a\",\"selectedProfileId\":\"profile-a\",\"script\":null}],"
           "\"profiles\":[{\"id\":\"profile-a\",\"controlMode\":\"lua\",\"script\":{"
           "\"enabled\":true,\"apiVersion\":\"1\",\"source\":\"" +
           std::string(source) + "\",\"sourceHash\":\"" + std::string(source_hash) +
           "\",\"bindings\":[],\"settings\":[],\"ui\":null}}]}";
}

TEST(ScriptCoordinator, PreparesCommitsAndPublishesRuntimeVisibility)
{
    std::atomic<bool> callback_visible = true;
    external_peepsight::ScriptCoordinator coordinator(
        [&callback_visible](external_peepsight::ScriptRuntimeUpdate update)
        { callback_visible.store(update.allows_visible, std::memory_order_release); });
    coordinator.start();
    const std::string source = "return eps.script { on_start=function(ctx) ctx.visibility:set(false) end }";
    const auto prepared = coordinator.prepare(
        snapshot_with_script(source, "964C6FC90B7C28631B817F76CA5F2E18C87BA5E02D2C09F3C6D84E85B462361C"));

    coordinator.commit(prepared.token);
    coordinator.stop();

    EXPECT_TRUE(prepared.profile_uses_lua);
    EXPECT_FALSE(prepared.allows_visible);
    EXPECT_TRUE(prepared.input_bindings.empty());
}

TEST(ScriptCoordinator, RejectsSourceHashMismatchWithoutReplacingRuntime)
{
    external_peepsight::ScriptCoordinator coordinator([](external_peepsight::ScriptRuntimeUpdate) {});
    coordinator.start();

    EXPECT_THROW(static_cast<void>(coordinator.prepare(snapshot_with_script(
                     "return eps.script {}", "0000000000000000000000000000000000000000000000000000000000000000"))),
                 std::invalid_argument);

    coordinator.stop();
}

TEST(ScriptCoordinator, ValidatesAndSerializesTrustedUiDeclarations)
{
    external_peepsight::ScriptCoordinator coordinator([](external_peepsight::ScriptRuntimeUpdate) {});
    coordinator.start();

    const std::string result = coordinator.validate(R"({
        "scope":"profile",
        "source":"return eps.script { api_version = \"2\", bindings = { toggle = { title = \"Toggle\", events = { \"pressed\" }, default_key = { device = \"mouse\", key = \"x1\", modifiers = { \"ctrl\" } } } }, settings = { enabled = { title = \"Enabled\", type = \"boolean\", default = true } }, ui = { sections = { { id = \"general\", title = \"General\", items = { { setting = \"enabled\", control = \"switch\" } } } } } }",
        "settings":[]
    })");

    coordinator.stop();

    EXPECT_NE(std::string::npos, result.find(R"("apiVersion":"2")"));
    EXPECT_NE(std::string::npos, result.find(R"("control":"switch")"));
    EXPECT_NE(std::string::npos, result.find(R"("settingId":"enabled")"));
    EXPECT_NE(std::string::npos, result.find(R"("defaultKey":{)"));
    EXPECT_NE(std::string::npos, result.find(R"("code":4)"));
    EXPECT_NE(std::string::npos, result.find(R"("device":"mouse")"));
    EXPECT_NE(std::string::npos, result.find(R"("modifiers":"ctrl")"));
}
} // namespace
