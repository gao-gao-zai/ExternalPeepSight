#include <gtest/gtest.h>
#include <windows.h>

TEST(NativeTestEnvironment, Starts)
{
    const DWORD processId = GetCurrentProcessId();

    EXPECT_NE(0U, processId);
}
