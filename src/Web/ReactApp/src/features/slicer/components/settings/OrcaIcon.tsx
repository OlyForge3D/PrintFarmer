/** Blue-tinted OrcaSlicer section icon */
export const OrcaIcon: React.FC<{ icon: string }> = ({ icon }) => (
  <img
    src={`/icons/orca/${icon}.svg`}
    alt=""
    width={16}
    height={16}
    className="shrink-0 filter-[invert(35%)_sepia(90%)_saturate(500%)_hue-rotate(190deg)_brightness(95%)]"
  />
);
