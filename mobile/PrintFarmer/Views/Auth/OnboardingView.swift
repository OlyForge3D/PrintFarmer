import SwiftUI

struct OnboardingView: View {
    @Binding var hasSeenOnboarding: Bool
    @State private var currentPage = 0
    
    var body: some View {
        VStack(spacing: 0) {
            // Skip button
            HStack {
                Spacer()
                Button {
                    completeOnboarding()
                } label: {
                    Text("Skip")
                        .font(.body)
                        .foregroundStyle(Color.pfTextSecondary)
                }
                .buttonStyle(.plain)
                .padding(.trailing, 20)
                .padding(.top, 16)
            }
            
            // Swipeable pages
            TabView(selection: $currentPage) {
                Page1()
                    .tag(0)
                Page2()
                    .tag(1)
                Page3()
                    .tag(2)
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            
            // Page indicator
            PageIndicator(currentPage: $currentPage, pageCount: 3, labels: ["Monitor", "Queue", "Alerts"])
                .padding(.bottom, 16)
        }
    }
    
    // MARK: - Pages
    
    @ViewBuilder
    private func Page1() -> some View {
        scrollablePage(VStack(spacing: 24) {
            Spacer()
            
            Image("AppLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 72, height: 72)
                .clipShape(RoundedRectangle(cornerRadius: 16))
            
            Text("Monitor Your Farm")
                .font(.title)
                .fontWeight(.bold)
                .multilineTextAlignment(.center)
            
            Text("Keep tabs on all your 3D printers in real-time. Monitor temperatures, progress, and status from anywhere.")
                .font(.body)
                .foregroundStyle(Color.pfTextSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 32)
            
            Spacer()
            Spacer()
        })
    }
    
    @ViewBuilder
    private func Page2() -> some View {
        scrollablePage(VStack(spacing: 24) {
            Spacer()
            
            Image(systemName: "tray.full.fill")
                .font(.system(size: 72))
                .foregroundStyle(Color.pfAccent)
                .accessibilityHidden(true)
            
            Text("Smart Job Queue")
                .font(.title)
                .fontWeight(.bold)
                .multilineTextAlignment(.center)
            
            Text("AutoDispatch intelligently manages your print queue. Jobs are automatically assigned to available printers.")
                .font(.body)
                .foregroundStyle(Color.pfTextSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 32)
            
            Spacer()
            Spacer()
        })
    }
    
    @ViewBuilder
    private func Page3() -> some View {
        scrollablePage(VStack(spacing: 24) {
            Spacer()
            
            Image(systemName: "bell.badge.fill")
                .font(.system(size: 72))
                .foregroundStyle(Color.pfAccent)
                .accessibilityHidden(true)
            
            Text("Stay Informed")
                .font(.title)
                .fontWeight(.bold)
                .multilineTextAlignment(.center)
            
            Text("Get notified about completed prints, maintenance needs, and temperature alerts.")
                .font(.body)
                .foregroundStyle(Color.pfTextSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 32)
            
            Spacer()
            
            Button {
                completeOnboarding()
            } label: {
                Text("Get Started")
                    .font(.headline)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity)
                    .padding()
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.pfButtonPrimary)
            .padding(.horizontal, 32)
            .padding(.bottom, 40)
        })
    }

    private func scrollablePage<Content: View>(_ content: Content) -> some View {
        GeometryReader { geometry in
            ScrollView {
                content
                    .frame(maxWidth: .infinity, minHeight: geometry.size.height)
            }
        }
    }
    
    // MARK: - Actions
    
    private func completeOnboarding() {
        hasSeenOnboarding = true
    }
}
