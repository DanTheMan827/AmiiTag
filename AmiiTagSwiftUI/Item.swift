//
//  Item.swift
//  AmiiTagSwiftUI
//
//  Created by Daniel Radtke on 2/3/25.
//  Copyright © 2025 Daniel Radtke. All rights reserved.
//

import Foundation
import SwiftData

@Model
final class Item {
    var timestamp: Date
    
    init(timestamp: Date) {
        self.timestamp = timestamp
    }
}
