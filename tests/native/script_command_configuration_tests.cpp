#include "script_command_configuration.h"

#include <gtest/gtest.h>

#include <string>
#include <vector>

namespace
{
[[nodiscard]] std::string snapshot()
{
    return R"({"schemaVersion":9,"activeProfileSetId":"set-a","profiles":[)"
           R"({"id":"profile-a"},{"id":"profile-b"},{"id":"profile-c"}],)"
           R"("profileSets":[)"
           R"({"id":"set-a","profileIds":["profile-a","profile-b"],"selectedProfileId":"profile-a"},)"
           R"({"id":"set-b","profileIds":["profile-c"],"selectedProfileId":"profile-c"}]})";
}

TEST(ScriptCommandConfiguration, AppliesOrderedProfileAndProfileSetSwitches)
{
    const std::string result = external_peepsight::apply_script_commands(
        snapshot(), {
                        {external_peepsight::ScriptCommandType::next_profile, {}},
                        {external_peepsight::ScriptCommandType::next_profile_set, {}},
                        {external_peepsight::ScriptCommandType::switch_profile_set, "set-a"},
                        {external_peepsight::ScriptCommandType::previous_profile, {}},
                    });

    EXPECT_NE(std::string::npos, result.find(R"("activeProfileSetId":"set-a")"));
    EXPECT_NE(std::string::npos,
              result.find(R"("id":"set-a","profileIds":["profile-a","profile-b"],"selectedProfileId":"profile-a")"));
}

TEST(ScriptCommandConfiguration, WrapsPreviousAndNextUsingStableArrayOrder)
{
    const std::string previous = external_peepsight::apply_script_commands(
        snapshot(), {{external_peepsight::ScriptCommandType::previous_profile, {}}});
    const std::string previous_set = external_peepsight::apply_script_commands(
        snapshot(), {{external_peepsight::ScriptCommandType::previous_profile_set, {}}});

    EXPECT_NE(std::string::npos, previous.find(R"("selectedProfileId":"profile-b")"));
    EXPECT_NE(std::string::npos, previous_set.find(R"("activeProfileSetId":"set-b")"));
}

TEST(ScriptCommandConfiguration, RejectsUnavailableExplicitTargets)
{
    EXPECT_THROW(static_cast<void>(external_peepsight::apply_script_commands(
                     snapshot(), {{external_peepsight::ScriptCommandType::switch_profile, "profile-c"}})),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::apply_script_commands(
                     snapshot(), {{external_peepsight::ScriptCommandType::switch_profile_set, "set-missing"}})),
                 std::invalid_argument);
}
} // namespace
