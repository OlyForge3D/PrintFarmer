namespace Farm.Infrastructure;

/// <summary>
/// Printer movement mechanism type defining the kinematic configuration.
/// </summary>
public enum MotionType
{
    /// <summary>
    /// Traditional 3-axis Cartesian system with independent XYZ movement.
    /// </summary>
    Cartesian = 0,

    /// <summary>
    /// CoreXY kinematics where X and Y motors work together for diagonal movement.
    /// </summary>
    CoreXY = 1,

    /// <summary>
    /// Delta kinematics with 3 towers and effector for precise movement.
    /// </summary>
    Delta = 2,

    /// <summary>
    /// Unknown or unspecified printer type.
    /// </summary>
    Unknown = 99
}
