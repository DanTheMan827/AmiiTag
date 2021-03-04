//
//  NFCMiFareTagError+LocalizedDescription.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/15/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import CoreNFC

extension NFCMiFareTagError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .crcError: return "CRC Error"
        case .eepromWriteError: return "EEPROM Write Error"
        case .invalidArgument: return "Invalid Argument"
        case .invalidAuthentication: return "Invalid Authentication"
        case .invalidData: return "Invalid Data"
        case .unknownError: return "Unknown Error"
        }
    }
}
