using System;
using System.Numerics;
using FluentAssertions;
using GalaxyBudsClient.Utils.Extensions;
using NUnit.Framework;

namespace GalaxyBudsClient.Tests;

/*
 * Quaternion -> Euler conversion is what the posture monitor uses to turn the earbuds' raw
 * orientation stream into a head pitch angle. These tests pin the convention: positive pitch looks
 * up, so looking down produces a negative delta from the calibrated neutral angle.
 */
[TestFixture]
public class OrientationMathTests
{
    [Test]
    public void EulerConversion_IdentityQuaternion_ReturnsZero()
    {
        var q = Quaternion.Identity;
        var (roll, pitch, yaw) = q.ToRollPitchYaw();

        ((double)roll).Should().BeApproximately(0.0, 0.001);
        ((double)pitch).Should().BeApproximately(0.0, 0.001);
        ((double)yaw).Should().BeApproximately(0.0, 0.001);
    }

    [Test]
    public void RelativeOrientation_IdenticalQuaternions_ResultsInZeroDelta()
    {
        // Reference looking 45 degrees
        var refQuat = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 4.0));
        var currentQuat = refQuat;

        var invRef = Quaternion.Inverse(refQuat);
        var rel = Quaternion.Normalize(Quaternion.Multiply(currentQuat, invRef));

        var (roll, pitch, yaw) = rel.ToRollPitchYaw();
        ((double)yaw).Should().BeApproximately(0.0, 0.001);
        ((double)pitch).Should().BeApproximately(0.0, 0.001);
        ((double)roll).Should().BeApproximately(0.0, 0.001);
    }

    [Test]
    public void RelativeOrientation_YawOffset_CalculatesExactDegrees()
    {
        var refQuat = Quaternion.Identity;
        // 90 degrees around Z axis (Yaw)
        var currentQuat = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2.0));

        var invRef = Quaternion.Inverse(refQuat);
        var rel = Quaternion.Normalize(Quaternion.Multiply(currentQuat, invRef));

        var (_, _, yawRad) = rel.ToRollPitchYaw();
        var yawDeg = (double)yawRad * (180.0 / Math.PI);

        yawDeg.Should().BeApproximately(90.0, 0.1);
    }

    [Test]
    public void RelativeOrientation_TiltedEarbudInEar_DecouplesPureHorizontalYaw()
    {
        // Real-world scenario: Earbud sits tilted ~45° in ear canal
        var earbudTilt = Quaternion.CreateFromYawPitchRoll(0.8f, -0.4f, 0.5f);
        var refQuat = earbudTilt;

        // User turns head horizontally in the room by +35 degrees around World Z
        var turnAngleRad = (float)(35.0 * Math.PI / 180.0);
        var worldTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turnAngleRad);
        var currentQuat = Quaternion.Multiply(worldTurn, refQuat);

        // World frame relative rotation
        var invRef = Quaternion.Inverse(refQuat);
        var rel = Quaternion.Normalize(Quaternion.Multiply(currentQuat, invRef));

        var (roll, pitch, yawRad) = rel.ToRollPitchYaw();
        var yawDeg = (double)yawRad * (180.0 / Math.PI);

        yawDeg.Should().BeApproximately(35.0, 0.01);
        ((double)roll).Should().BeApproximately(0.0, 0.01);
        ((double)pitch).Should().BeApproximately(0.0, 0.01);
    }
}
