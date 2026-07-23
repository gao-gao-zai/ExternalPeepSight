#include "render_state.h"

#include <gtest/gtest.h>

namespace
{
[[nodiscard]] external_peepsight::ToastMessage message(std::string key, const std::int32_t priority,
                                                       std::optional<std::uint32_t> duration = std::nullopt)
{
    return {key, key, std::wstring(key.begin(), key.end()), "test", priority, duration};
}

TEST(ToastQueue, ReplacesDuplicateAndPrioritizesProfileMessages)
{
    external_peepsight::ToastQueue queue;

    EXPECT_EQ("switch", queue.push(message("switch", 0), 100U, 1'000U).message.id);
    EXPECT_EQ("switch", queue.push(message("switch", 0), 200U, 1'000U).message.id);
    EXPECT_EQ(1'200U, queue.active()->expires_at_ms);
    EXPECT_EQ("profile", queue.push(message("profile", 10), 300U, 1'000U).message.id);

    const auto next = queue.advance(1'300U, 1'000U);
    ASSERT_TRUE(next);
    EXPECT_EQ("switch", next->message.id);
}

TEST(ToastQueue, QueuesLowerPriorityAndHonorsCustomDuration)
{
    external_peepsight::ToastQueue queue;

    static_cast<void>(queue.push(message("high", 5, 500U), 0U, 1'000U));
    EXPECT_EQ("high", queue.push(message("low", 1), 100U, 1'000U).message.id);
    EXPECT_EQ("high", queue.advance(499U, 1'000U)->message.id);
    EXPECT_EQ("low", queue.advance(500U, 1'000U)->message.id);
}

TEST(RenderUpdateCoalescer, AppliesOnlyNewestVersionAtSixteenMillisecondBoundary)
{
    external_peepsight::RenderUpdateCoalescer coalescer;

    EXPECT_TRUE(coalescer.submit(1U, 100U));
    EXPECT_TRUE(coalescer.submit(2U, 110U));
    EXPECT_FALSE(coalescer.submit(1U, 111U));
    EXPECT_EQ(116U, coalescer.due_at_ms());
    EXPECT_FALSE(coalescer.take_due(115U));
    EXPECT_EQ(2U, coalescer.take_due(116U));
    EXPECT_FALSE(coalescer.submit(2U, 200U));
    EXPECT_TRUE(coalescer.submit(3U, 200U));
}

TEST(RenderStateParsing, RejectsUnknownToastFieldsAndOutOfRangePriority)
{
    EXPECT_THROW(static_cast<void>(external_peepsight::parse_toast_message(
                     R"({"id":"1","deduplicationKey":"a","text":"ok","category":"switch","extra":true})")),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::parse_toast_message(
                     R"({"id":"1","deduplicationKey":"a","text":"ok","category":"switch","priority":101})")),
                 std::invalid_argument);
}
} // namespace
