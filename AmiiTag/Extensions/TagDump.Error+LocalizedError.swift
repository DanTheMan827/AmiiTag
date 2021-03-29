//
//  TagDump.Error+LocalizedError.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/28/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

extension TagDump.Error: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .invalidUID: return "Invalid UID"
        }
    }
}
