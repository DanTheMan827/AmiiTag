//
//  StatusResult.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/5/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

public enum StatusResult<Result, Status, Error> {
    case success(Result)
    case failure(Error)
    case status(Status)
}
